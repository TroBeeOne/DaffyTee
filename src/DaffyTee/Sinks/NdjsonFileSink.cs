namespace DaffyTee.Sinks;

/// <summary>
/// Events as newline-delimited JSON in a local file - one CloudEvents document per line, exactly
/// what RabbitMQ would carry. For a developer who wants the stream without a broker, for tests,
/// and for anything that already ingests JSON lines (jq, Splunk, Elastic, Loki, a spreadsheet).
/// </summary>
internal sealed class NdjsonFileSink(string template) : IEventSink
{
    private readonly RollingFileWriter _file = new(template);

    public string Description => $"file {_file.Template}";
    public SinkState State { get; private set; } = SinkState.Healthy;
    public string? LastError { get; private set; }

    public Task<int> PublishAsync(IReadOnlyList<OutboundEvent> batch, CancellationToken cancellationToken)
    {
        try
        {
            _file.WriteLines(batch.Select(e => (ReadOnlyMemory<byte>)e.Body));
            State = SinkState.Healthy;
            return Task.FromResult(0);
        }
        catch (IOException ex)
        {
            // A full disk or a locked file is worth retrying, like an unreachable broker.
            State = SinkState.Unavailable;
            LastError = ex.Message;
            throw new SinkUnavailableException($"cannot write {_file.CurrentPath}: {ex.Message}", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        State = SinkState.Closed;
        _file.Dispose();
        return ValueTask.CompletedTask;
    }
}
