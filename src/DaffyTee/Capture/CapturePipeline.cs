using System.Diagnostics;
using System.Threading.Channels;
using DaffyTee.Proxy;
using DaffyTee.Sinks;

namespace DaffyTee.Capture;

/// <summary>
/// Everything between the relay and the outside world: a queue bounded by <see cref="CaptureBudget"/>,
/// one worker that decodes and publishes, and the bookkeeping that makes the stream trustworthy -
/// gap markers where events were lost, heartbeats while nothing happens, start and stop markers
/// around the proxy's life, and control markers when someone changes the settings.
///
/// One worker keeps each session's events in order. Decoding is cheap next to network I/O, and a
/// sink call carries a whole batch, so one is plenty; the relay never waits for it either way.
///
/// When the sink is unavailable the worker holds its batch and retries with backoff, and events
/// pile up behind it until the budget says stop - after which new ones are dropped and counted. The
/// sink's health is logged when it changes, and then as a reminder every few minutes, not on every
/// failed attempt: a proxy that runs for months should not fill a disk with the same line.
/// </summary>
internal sealed class CapturePipeline : IAsyncDisposable
{
    private const int BatchSize = 256;
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Retries back off to this and no further, so a broker that comes back is noticed within
    /// seconds even after a long outage, while a broker that stays down costs one attempt per interval.
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gapGate = new();
    private readonly Dictionary<int, SessionDecodeState> _decodeState = [];
    private readonly Func<int> _activeSessions;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _worker, _heartbeat;
    private long _captured, _published, _rejected, _events;
    private volatile bool _purgeRequested;
    private DateTime? _lastPublishedUtc, _unavailableSinceUtc;
    private DateTime _lastReminderUtc;
    private bool _warnedBacklog, _warnedDropping;
    private long _dropsAtLastWarning;

    public CapturePipeline(TeeSettings settings, IEventSink? sink, RollingFileWriter? sqlLog, Func<int> activeSessions,
                           TimeSpan heartbeatInterval)
    {
        Settings = settings;
        Budget = new CaptureBudget(settings.MaxQueueBytes, settings.MaxQueueEvents);
        Sink = sink;
        SqlLog = sqlLog;
        _activeSessions = activeSessions;
        HeartbeatInterval = heartbeatInterval;
        Source = $"daffytee://{settings.Instance}";
    }

    public TeeSettings Settings { get; }
    public CaptureBudget Budget { get; }
    public RecentEvents Recent { get; } = new();
    public IEventSink? Sink { get; }
    public RollingFileWriter? SqlLog { get; }
    public string Source { get; }
    public TimeSpan HeartbeatInterval { get; }

    public long Captured => Interlocked.Read(ref _captured);
    public long Published => Interlocked.Read(ref _published);
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>Events handed to the local outputs (recent list, :watch, SQL log), sink or not.</summary>
    public long Delivered => Interlocked.Read(ref _events);
    public DateTime? LastPublishedUtc => _lastPublishedUtc;
    public DateTime? UnavailableSinceUtc => _unavailableSinceUtc;

    /// <summary>A queued item: an exchange still to decode, or an event ready to go.</summary>
    private readonly record struct QueueItem(CapturedExchange? Exchange, AuditEvent? Event);

    public void Start()
    {
        _worker = Task.Run(() => WorkAsync(_stopping.Token));
        _heartbeat = Task.Run(() => HeartbeatAsync(_stopping.Token));
        Publish(ProxyEvent("proxy.start", ("teeEnabled", Settings.Enabled), ("settings", Settings.Describe()),
                                          ("redactSecrets", Settings.RedactSecrets),
                                          ("sink", Sink?.Description ?? "none"), ("version", Program.ToolVersion())));
    }

    // ---- producers (relay threads) ---------------------------------------------------------------

    /// <summary>Queues a finished exchange, or drops it (counted) when the queue is full.</summary>
    public bool Submit(CapturedExchange exchange)
    {
        if (!Budget.TryReserveEvent())
        {
            Budget.RecordDrop(DropReason.QueueFull, exchange.RequestBytes);
            exchange.Release(Budget);
            return false;
        }

        Interlocked.Increment(ref _captured);
        Enqueue(new QueueItem(exchange, null));
        return true;
    }

    /// <summary>
    /// Queues a ready-made event. Proxy events always get in - they are how consumers learn about
    /// gaps and health - while session events are subject to the tee switch and the queue limit.
    /// </summary>
    public void Publish(AuditEvent e)
    {
        bool proxyEvent = e.Type.StartsWith("proxy.", StringComparison.Ordinal);
        if (!proxyEvent && !Settings.Enabled) return;

        if (proxyEvent) Budget.ForceReserveEvent();
        else if (!Budget.TryReserveEvent())
        {
            Budget.RecordDrop(DropReason.QueueFull, 0);
            return;
        }

        Enqueue(new QueueItem(null, e));
    }

    private void Enqueue(QueueItem item)
    {
        // A gap marker goes in ahead of the first event to get through after a drop, so it sits
        // where the loss happened in the stream.
        if (Budget.HasPendingGap) EmitPendingGap();
        _channel.Writer.TryWrite(item);
    }

    private void EmitPendingGap()
    {
        lock (_gapGate)
        {
            if (Budget.TakeGap() is not { } gap) return;
            Budget.ForceReserveEvent();
            _channel.Writer.TryWrite(new QueueItem(null, GapEvent(gap)));
        }
    }

    public void SessionStarted(ProxySession session) => Publish(new AuditEvent
    {
        Type = "session.start",
        TimeUtc = DateTime.UtcNow,
        Session = SessionSnapshot.From(session),
        Data =
        [
            new("clientSecurity", session.FrontSecurity),
            new("targetSecurity", session.UpstreamSecurity),
            new("serverVersion", session.ServerVersion),
            new("mars", session.Mars),
        ],
    });

    public void SessionEnded(ProxySession session) => Publish(new AuditEvent
    {
        Type = "session.end",
        TimeUtc = DateTime.UtcNow,
        Session = SessionSnapshot.From(session),
        DurationMs = Math.Round((DateTime.UtcNow - session.StartedUtc).TotalMilliseconds, 1),
        Data =
        [
            new("requests", session.Requests),
            new("bytesFromClient", session.BytesFromClient),
            new("bytesFromTarget", session.BytesFromServer),
            new("reason", session.CloseReason),
        ],
    });

    public void LoginRefused(ProxySession session, string login, string code) => Publish(new AuditEvent
    {
        Type = "session.login_failed",
        TimeUtc = DateTime.UtcNow,
        Data =
        [
            new("login", login),
            new("reason", code),
            new("client", session.ClientAddress),
            new("host", session.HostName),
            new("app", session.AppName),
            new("database", session.Database),
            new("clientSecurity", session.FrontSecurity),
        ],
    });

    /// <summary>
    /// Records a settings change in the log and the stream, so a quiet period is explained - with
    /// who made it: the console when <paramref name="login"/> is null, otherwise that DaffyTee login,
    /// from its <paramref name="session"/>, in T-SQL.
    /// </summary>
    public void Control(string change, string? login = null, int session = 0)
    {
        Log.Info("tee.changed", login is null ? $"{change} (from the console)" : $"{change} (by '{login}' in T-SQL, session {session})", session);
        Publish(ProxyEvent("proxy.control",
            ("change", change),
            ("by", login is null ? "console" : login),
            ("session", login is null ? null : session),
            ("teeEnabled", Settings.Enabled),
            ("settings", Settings.Describe())));
    }

    /// <summary>The capture's state at a glance - for :status at the prompt and daffytee.health in T-SQL alike.</summary>
    public CaptureHealth Health() => new(
        Settings.Describe(),
        Sink is null ? "no sink" : Sink.Description,
        SqlLog?.CurrentPath,
        Sink?.State.ToString().ToLowerInvariant(),
        UnavailableSinceUtc,
        Sink is { State: not SinkState.Healthy } ? Sink.LastError : null,
        LastPublishedUtc,
        Budget.Events, Budget.Bytes, Budget.MaxBytes, Budget.MaxEvents, Budget.Fill,
        Captured, Published, Budget.DroppedTotal,
        Budget.Dropped(DropReason.QueueFull), Budget.Dropped(DropReason.Purged),
        Budget.Dropped(DropReason.SinkRejected), Budget.Dropped(DropReason.Shutdown));

    /// <summary>Empties the queue now; the loss is reported like any other, with reason "purged".</summary>
    public void Purge() => _purgeRequested = true;

    // ---- the worker ------------------------------------------------------------------------------

    private async Task WorkAsync(CancellationToken stopping)
    {
        var reader = _channel.Reader;
        var items = new List<QueueItem>(BatchSize);
        var outbound = new List<OutboundEvent>(BatchSize);

        while (await reader.WaitToReadAsync(CancellationToken.None))
        {
            items.Clear();
            outbound.Clear();

            if (_purgeRequested) DrainAsPurged(reader);

            while (items.Count < BatchSize && reader.TryRead(out var item)) items.Add(item);
            if (items.Count == 0) continue;

            foreach (var item in items) Deliver(item.Event ?? Decode(item.Exchange!), outbound);

            int count = items.Count;
            if (Sink is not null && outbound.Count > 0) await PublishWithRetryAsync(outbound, stopping);
            else Interlocked.Add(ref _published, count);

            // The raw copies are held - and counted against the budget - until delivery is confirmed,
            // so the budget measures what is really waiting, decoded or not.
            foreach (var item in items) item.Exchange?.Release(Budget);
            for (int i = 0; i < count; i++) Budget.ReleaseEvent();

            // A loss is reported as soon as there is room to report it, not whenever the next event
            // happens to come along - on a quiet system that could be the next heartbeat, or never.
            if (Budget.HasPendingGap && Budget.Fill < 0.9) EmitPendingGap();

            WatchBacklog();
        }
    }

    private AuditEvent Decode(CapturedExchange exchange)
    {
        if (!_decodeState.TryGetValue(exchange.Session.Id, out var state))
            _decodeState[exchange.Session.Id] = state = new SessionDecodeState { Database = exchange.Session.Database };

        return EventDecoder.Decode(exchange, state, Settings.IncludeRaw, Settings.RedactSecrets);
    }

    /// <summary>Local outputs first - the recent list, :watch, the SQL log - then the sink's batch.</summary>
    private void Deliver(AuditEvent e, List<OutboundEvent> outbound)
    {
        Interlocked.Increment(ref _events);
        Recent.Add(e);

        // Heartbeats prove liveness to queue consumers; in a developer's SQL file they are only a line a minute of noise.
        if (SqlLog is not null && e.Type != "proxy.heartbeat")
        {
            try { SqlLog.Write(SqlText.Render(e)); }
            catch (IOException ex) { Log.Warn("sqllog.failed", $"could not write the SQL log: {ex.Message}"); }
        }

        if (e.Type == "session.end" && e.Session is { } ended) _decodeState.Remove(ended.Id);

        if (Sink is null) return;

        var headers = new Dictionary<string, string> { ["daffytee-instance"] = Settings.Instance };
        if (e.Session is { } s)
        {
            headers["daffytee-session"] = s.Id.ToString();
            headers["daffytee-login"] = s.Login;
            headers["daffytee-target"] = s.Target;
            if (s.Database is not null) headers["daffytee-database"] = s.Database;
            if (s.App.Length > 0) headers["daffytee-app"] = s.App;
        }

        outbound.Add(new OutboundEvent(e.Type, e.PartitionKey, e.Id, e.TimeUtc, EventJson.Serialize(e, Source), headers));
    }

    private async Task PublishWithRetryAsync(List<OutboundEvent> batch, CancellationToken stopping)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            try
            {
                int rejected = await Sink!.PublishAsync(batch, stopping);
                Interlocked.Add(ref _published, batch.Count - rejected);
                if (rejected > 0)
                {
                    Interlocked.Add(ref _rejected, rejected);
                    Budget.RecordDrop(DropReason.SinkRejected, 0, rejected);
                }
                _lastPublishedUtc = DateTime.UtcNow;
                SinkRecovered();
                return;
            }
            catch (Exception ex) when (ex is SinkUnavailableException or OperationCanceledException)
            {
                if (stopping.IsCancellationRequested)
                {
                    Budget.RecordDrop(DropReason.Shutdown, batch.Sum(b => (long)b.Body.Length), batch.Count);
                    return;
                }

                SinkUnavailable(ex.Message);

                // The worker is parked here for the whole outage, so this is where the queue filling
                // up has to be noticed - after the batch would be far too late.
                WatchBacklog();

                if (_purgeRequested)
                {
                    Budget.RecordDrop(DropReason.Purged, batch.Sum(b => (long)b.Body.Length), batch.Count);
                    return;
                }

                try { await Task.Delay(backoff, stopping); } catch (OperationCanceledException) { }
                backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
            }
        }
    }

    private void DrainAsPurged(ChannelReader<QueueItem> reader)
    {
        _purgeRequested = false;
        long events = 0, bytes = 0;
        while (reader.TryRead(out var item))
        {
            events++;
            if (item.Exchange is { } exchange)
            {
                bytes += exchange.RequestBytes;
                exchange.Release(Budget);
            }
            Budget.ReleaseEvent();
        }

        if (events > 0)
        {
            Budget.RecordDrop(DropReason.Purged, bytes, events);
            Log.Warn("queue.purged", $"purged {events:N0} queued event(s), {bytes / 1024.0 / 1024:F1} MB; a gap marker will follow");
        }
    }

    // ---- health reporting ------------------------------------------------------------------------

    private void SinkUnavailable(string reason)
    {
        var now = DateTime.UtcNow;
        if (_unavailableSinceUtc is null)
        {
            _unavailableSinceUtc = now;
            _lastReminderUtc = now;
            Log.Warn("sink.unavailable",
                $"{Sink!.Description} is unavailable ({reason}); events are held in memory " +
                $"(up to {Budget.MaxBytes / 1024 / 1024} MB / {Budget.MaxEvents:N0} events) and retried. The database path is unaffected.");
            return;
        }

        if (now - _lastReminderUtc >= ReminderInterval)
        {
            _lastReminderUtc = now;
            Log.Warn("sink.still_unavailable",
                $"{Sink!.Description} still unavailable after {Elapsed(now - _unavailableSinceUtc.Value)} ({reason}); " +
                $"backlog {Budget.Events:N0} event(s), {Budget.Bytes / 1024.0 / 1024:F1} MB ({Budget.Fill:P0}), {Budget.DroppedTotal:N0} dropped so far");
        }
    }

    private void SinkRecovered()
    {
        if (_unavailableSinceUtc is not { } since) return;
        _unavailableSinceUtc = null;
        Log.Info("sink.recovered",
            $"{Sink!.Description} is back after {Elapsed(DateTime.UtcNow - since)}; delivering a backlog of {Budget.Events:N0} event(s)");
    }

    /// <summary>Warns once as the queue passes half full, once when it starts dropping, and again when it clears.</summary>
    private void WatchBacklog()
    {
        double fill = Budget.Fill;
        if (!_warnedBacklog && fill >= 0.5)
        {
            _warnedBacklog = true;
            Log.Warn("queue.backlog", $"capture queue is {fill:P0} full ({Budget.Events:N0} events, {Budget.Bytes / 1024.0 / 1024:F1} MB)");
        }
        else if (_warnedBacklog && fill < 0.25)
        {
            _warnedBacklog = false;
            Log.Info("queue.drained", "capture queue is back under 25%");
        }

        long dropped = Budget.DroppedTotal;
        if (!_warnedDropping && dropped > _dropsAtLastWarning)
        {
            _warnedDropping = true;
            Log.Warn("queue.dropping", "capture queue is full: new events are being dropped (and counted) until it drains");
        }
        else if (_warnedDropping && fill < 0.9)
        {
            _warnedDropping = false;
            Log.Info("queue.recovered", $"capture queue has room again; {dropped - _dropsAtLastWarning:N0} event(s) were dropped, reported as a gap");
            _dropsAtLastWarning = dropped;
        }
    }

    private static string Elapsed(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:F0}s" : span.TotalHours < 1 ? $"{span.TotalMinutes:F0}m" : $"{span.TotalHours:F1}h";

    // ---- proxy events ----------------------------------------------------------------------------

    private async Task HeartbeatAsync(CancellationToken stopping)
    {
        if (HeartbeatInterval <= TimeSpan.Zero) return;
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping)) Publish(HeartbeatEvent());
        }
        catch (OperationCanceledException) { }
    }

    private AuditEvent HeartbeatEvent() => ProxyEvent("proxy.heartbeat",
        ("teeEnabled", Settings.Enabled),
        ("sessions", _activeSessions()),
        ("captured", Captured),
        ("published", Published),
        ("dropped", Budget.DroppedTotal),
        ("queueEvents", Budget.Events),
        ("queueBytes", Budget.Bytes),
        ("sinkState", Sink?.State.ToString().ToLowerInvariant() ?? "none"),
        ("uptimeSeconds", (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds));

    private static AuditEvent GapEvent(Gap gap) => ProxyEvent("proxy.gap",
        ("dropped", gap.Events),
        ("droppedBytes", gap.Bytes),
        // Offsets, not bare DateTimes: a bare DateTime is written without a zone, as SQL values are.
        ("from", new DateTimeOffset(gap.FromUtc, TimeSpan.Zero)),
        ("to", new DateTimeOffset(gap.ToUtc, TimeSpan.Zero)),
        ("reasons", gap.Reasons.Select(r => new KeyValuePair<string, object?>(r.Key, r.Value)).ToList()));

    private static AuditEvent ProxyEvent(string type, params (string Key, object? Value)[] data) => new()
    {
        Type = type,
        TimeUtc = DateTime.UtcNow,
        Data = data.Where(d => d.Value is not null).Select(d => new KeyValuePair<string, object?>(d.Key, d.Value)).ToList(),
    };

    /// <summary>
    /// Announces the stop, then gives the worker up to <paramref name="drain"/> to deliver what is
    /// queued. Whatever is still undelivered after that is counted as dropped for shutdown.
    /// </summary>
    public async Task StopAsync(TimeSpan drain)
    {
        Publish(ProxyEvent("proxy.stop", ("captured", Captured), ("published", Published), ("dropped", Budget.DroppedTotal)));
        _channel.Writer.TryComplete();

        if (_worker is not null)
        {
            var finished = await Task.WhenAny(_worker, Task.Delay(drain));
            if (finished != _worker)
            {
                Log.Warn("queue.shutdown_undelivered", $"stopping with {Budget.Events:N0} event(s) undelivered after waiting {drain.TotalSeconds:F0}s");
                _stopping.Cancel();
                try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
        }

        _stopping.Cancel();
        if (_heartbeat is not null) { try { await _heartbeat; } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (Sink is not null) await Sink.DisposeAsync();
        SqlLog?.Dispose();
    }
}

/// <summary>What the capture is doing and how its queue stands, read in one go.</summary>
internal sealed record CaptureHealth(
    string Tee, string Outputs, string? SqlLog, string? SinkState, DateTime? UnavailableSinceUtc, string? SinkError,
    DateTime? LastDeliveryUtc, long QueueEvents, long QueueBytes, long QueueMaxBytes, int QueueMaxEvents, double QueueFill,
    long Captured, long Delivered, long Dropped, long DroppedQueueFull, long DroppedPurged, long DroppedRejected,
    long DroppedShutdown);
