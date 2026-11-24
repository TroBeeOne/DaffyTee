namespace DaffyTee.Sinks;

/// <summary>An event ready to leave the process: its routing, its identity, and its body.</summary>
/// <param name="RoutingKey">The event type, e.g. request.rpc - a RabbitMQ routing key, or a Kafka topic suffix.</param>
/// <param name="PartitionKey">Events sharing this key must stay in order (one per session).</param>
internal sealed record OutboundEvent(string RoutingKey, string PartitionKey, Guid Id, DateTime TimeUtc,
                                     byte[] Body, IReadOnlyDictionary<string, string> Headers);

internal enum SinkState { Connecting, Healthy, Unavailable, Closed }

/// <summary>A sink that cannot take events right now; the pipeline keeps them and tries again.</summary>
internal sealed class SinkUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Where published events go. Exactly one sink is active - RabbitMQ, a file, or (later) Kafka - and
/// the pipeline above it neither knows nor cares which: batching, retrying, the memory budget, gap
/// markers and heartbeats all live there, so a sink only has to deliver or say it could not.
///
/// <see cref="PublishAsync"/> returns once every event in the batch is safely delivered (for a
/// broker, confirmed), and throws <see cref="SinkUnavailableException"/> when delivery is not
/// possible now. Events it refuses outright (unroutable, rejected) are reported through the count
/// it returns, not by throwing, so one bad event cannot hold up the rest.
/// </summary>
internal interface IEventSink : IAsyncDisposable
{
    /// <summary>Human description for :status and the banner - never includes credentials.</summary>
    string Description { get; }

    SinkState State { get; }
    string? LastError { get; }

    /// <summary>Returns how many of the batch were rejected (delivered nowhere).</summary>
    Task<int> PublishAsync(IReadOnlyList<OutboundEvent> batch, CancellationToken cancellationToken);
}
