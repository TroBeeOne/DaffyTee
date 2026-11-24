namespace DaffyTee.Capture;

/// <summary>
/// The last few hundred events, kept in memory for :recent and streamed live to :watch. Independent
/// of any sink - a developer can watch what an application sends with no queue configured at all.
/// </summary>
internal sealed class RecentEvents(int capacity = 500)
{
    private readonly Queue<AuditEvent> _events = new();
    private readonly object _gate = new();

    /// <summary>Set while :watch is running. Called on the pipeline worker; must not block for long.</summary>
    public Action<AuditEvent>? Watcher { get; set; }

    public void Add(AuditEvent e)
    {
        lock (_gate)
        {
            _events.Enqueue(e);
            while (_events.Count > capacity) _events.Dequeue();
        }

        try { Watcher?.Invoke(e); }
        catch { /* a broken watcher must not stop the pipeline */ }
    }

    public IReadOnlyList<AuditEvent> Tail(int count)
    {
        lock (_gate) return _events.Skip(Math.Max(0, _events.Count - count)).ToList();
    }
}
