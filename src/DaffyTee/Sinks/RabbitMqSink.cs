using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace DaffyTee.Sinks;

/// <summary>Where and how to publish to RabbitMQ.</summary>
/// <param name="Uri">amqp[s]://user:password@host:port/vhost - credentials included, so never logged.</param>
/// <param name="Exchange">A durable topic exchange, declared if missing.</param>
/// <param name="DeclareQueue">Optionally also declare this durable queue and bind it to everything ("#").</param>
internal sealed record RabbitSettings(string Uri, string Exchange = "daffytee", string? DeclareQueue = null)
{
    /// <summary>The URI without its password, for logs, the banner and :status.</summary>
    public string SafeDescription
    {
        get
        {
            if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var parsed)) return "rabbitmq (invalid URI)";
            string user = System.Uri.UnescapeDataString(parsed.UserInfo.Split(':')[0]);
            string vhost = System.Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'));
            int port = parsed.IsDefaultPort || parsed.Port < 0 ? (parsed.Scheme == "amqps" ? 5671 : 5672) : parsed.Port;
            return $"rabbitmq {parsed.Scheme}://{(user.Length > 0 ? user + "@" : "")}{parsed.Host}:{port} " +
                   $"vhost={(vhost.Length == 0 ? "/" : vhost)} exchange={Exchange}";
        }
    }
}

/// <summary>
/// Publishes events to a RabbitMQ topic exchange, each one persistent and each one confirmed by the
/// broker before it counts as delivered.
///
/// Events are published as CloudEvents in structured mode (the whole event is the JSON body,
/// content type application/cloudevents+json) with the event type as the routing key -
/// request.rpc, session.start, proxy.gap and so on - so consumers pick what they want with
/// bindings such as "request.*" or "session.#". A few headers (login, target, database, app) make
/// a headers exchange possible too.
///
/// Messages are published mandatory: a topic exchange silently discards what no queue is bound to,
/// and an audit stream should not lose events without saying so. Those returns are counted as
/// rejected - and warned about - rather than retried, since retrying cannot make a binding appear.
///
/// The connection is DaffyTee's own to manage: when anything goes wrong it is dropped and rebuilt on
/// the next attempt, and the pipeline decides how often that is. A batch that fails part-way is
/// retried whole, so delivery is at least once; consumers de-duplicate on the event id.
/// </summary>
internal sealed class RabbitMqSink(RabbitSettings settings) : IEventSink
{
    private const string ContentType = "application/cloudevents+json";
    private const int MaxOutstandingConfirms = 512;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _warnedUnroutable;

    public string Description => settings.SafeDescription;
    public SinkState State { get; private set; } = SinkState.Connecting;
    public string? LastError { get; private set; }

    public async Task<int> PublishAsync(IReadOnlyList<OutboundEvent> batch, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await EnsureChannelAsync(cancellationToken);

            var publishes = new List<(OutboundEvent Event, Task Task)>(batch.Count);
            foreach (var e in batch)
            {
                var properties = new BasicProperties
                {
                    Persistent = true,
                    ContentType = ContentType,
                    MessageId = e.Id.ToString(),
                    Type = e.RoutingKey,
                    AppId = "daffytee",
                    Timestamp = new AmqpTimestamp(new DateTimeOffset(DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()),
                    Headers = e.Headers.ToDictionary(h => h.Key, h => (object?)h.Value),
                };

                publishes.Add((e, channel.BasicPublishAsync(settings.Exchange, e.RoutingKey, mandatory: true,
                                                            properties, e.Body, cancellationToken).AsTask()));
            }

            int rejected = 0;
            foreach (var (e, task) in publishes)
            {
                try
                {
                    await task;
                }
                catch (PublishException ex) when (ex.IsReturn)
                {
                    rejected++;
                    WarnUnroutable(e.RoutingKey);
                }
                catch (PublishException)
                {
                    // Nacked: the broker could not take it (a full disk, a queue length limit).
                    rejected++;
                }
            }

            State = SinkState.Healthy;
            LastError = null;
            return rejected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            State = SinkState.Unavailable;
            LastError = Describe(ex);
            await DropConnectionAsync();
            throw new SinkUnavailableException(LastError, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } && _connection is { IsOpen: true }) return _channel;
        await DropConnectionAsync();

        State = SinkState.Connecting;
        var factory = new ConnectionFactory
        {
            Uri = new Uri(settings.Uri),
            ClientProvidedName = $"daffytee@{Environment.MachineName}",
            AutomaticRecoveryEnabled = false,       // the pipeline's retry loop is the recovery
            TopologyRecoveryEnabled = false,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true,
            outstandingPublisherConfirmationsRateLimiter: new ThrottlingRateLimiter(MaxOutstandingConfirms)), cancellationToken);

        await _channel.ExchangeDeclareAsync(settings.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
                                            cancellationToken: cancellationToken);

        if (settings.DeclareQueue is { } queue)
        {
            await _channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                                             cancellationToken: cancellationToken);
            await _channel.QueueBindAsync(queue, settings.Exchange, "#", cancellationToken: cancellationToken);
        }

        Log.Info("sink.connected", $"connected to {Description}" + (settings.DeclareQueue is null ? "" : $", queue '{settings.DeclareQueue}' bound to #"));
        return _channel;
    }

    private void WarnUnroutable(string routingKey)
    {
        if (_warnedUnroutable) return;
        _warnedUnroutable = true;
        Log.Warn("sink.unroutable",
            $"RabbitMQ returned a '{routingKey}' event as unroutable: no queue is bound to exchange '{settings.Exchange}' for it. " +
            "Such events are counted as rejected; bind a queue (or start with --rabbit-queue NAME) to keep them.");
    }

    private async Task DropConnectionAsync()
    {
        try { if (_channel is not null) await _channel.DisposeAsync(); } catch { }
        try { if (_connection is not null) await _connection.DisposeAsync(); } catch { }
        _channel = null;
        _connection = null;
    }

    private static string Describe(Exception ex) => ex switch
    {
        BrokerUnreachableException { InnerException: { } inner } => $"broker unreachable: {Innermost(inner)}",
        AuthenticationFailureException or PossibleAuthenticationFailureException => "authentication failed - check the user and password in the RabbitMQ URI",
        AlreadyClosedException closed => $"connection closed: {closed.ShutdownReason?.ReplyText ?? closed.Message}",
        OperationInterruptedException interrupted => $"operation interrupted: {interrupted.ShutdownReason?.ReplyText ?? interrupted.Message}",
        _ => Innermost(ex),
    };

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    public async ValueTask DisposeAsync()
    {
        State = SinkState.Closed;
        if (_channel is { IsOpen: true } channel)
        {
            try { await channel.CloseAsync(); } catch { }
        }
        await DropConnectionAsync();
        _gate.Dispose();
    }
}
