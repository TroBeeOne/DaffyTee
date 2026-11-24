namespace DaffyTee.Capture;

/// <summary>Why a captured event did not make it out.</summary>
internal enum DropReason { QueueFull, Purged, SinkRejected, Shutdown }

/// <summary>A stretch of events that were dropped, reported to consumers as a proxy.gap event.</summary>
internal sealed record Gap(long Events, long Bytes, DateTime FromUtc, DateTime ToUtc, IReadOnlyDictionary<string, long> Reasons);

/// <summary>
/// Bounds the memory the tee may use, in bytes and in events, so that however long the queue is
/// unreachable, the proxy's own memory stays flat and the database path is never affected.
///
/// Bytes are reserved as a request is copied, not when it is queued, so a slow sink cannot be
/// outrun by a fast client: once the budget is spent, new requests are simply not copied (they are
/// still relayed, of course) and are counted as dropped. That is "drop newest": the backlog that
/// built up first is kept and delivered in order when the sink comes back.
///
/// Every drop is remembered until the next event gets through, at which point the pipeline puts a
/// gap marker in front of it - so a consumer can always tell "nothing happened" from "we lost some".
/// </summary>
internal sealed class CaptureBudget(long maxBytes, int maxEvents)
{
    private readonly object _gapGate = new();
    private long _bytes;
    private int _events;

    private long _gapEvents, _gapBytes;
    private DateTime _gapFrom, _gapTo;
    private readonly Dictionary<string, long> _gapReasons = [];

    private long _droppedTotal, _droppedBytesTotal;
    private readonly long[] _droppedByReason = new long[Enum.GetValues<DropReason>().Length];

    public long MaxBytes { get; } = maxBytes;
    public int MaxEvents { get; } = maxEvents;
    public long Bytes => Interlocked.Read(ref _bytes);
    public int Events => Volatile.Read(ref _events);
    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);
    public long DroppedBytesTotal => Interlocked.Read(ref _droppedBytesTotal);
    public long Dropped(DropReason reason) => Interlocked.Read(ref _droppedByReason[(int)reason]);

    /// <summary>How full, 0..1, by whichever limit is closer.</summary>
    public double Fill => Math.Max((double)Bytes / MaxBytes, (double)Events / MaxEvents);

    public bool HasPendingGap => Interlocked.Read(ref _gapEvents) > 0;

    public bool TryReserveBytes(int count)
    {
        if (Interlocked.Add(ref _bytes, count) <= MaxBytes) return true;
        Interlocked.Add(ref _bytes, -count);
        return false;
    }

    public void ReleaseBytes(int count)
    {
        if (count != 0) Interlocked.Add(ref _bytes, -count);
    }

    public bool TryReserveEvent()
    {
        if (Interlocked.Increment(ref _events) <= MaxEvents) return true;
        Interlocked.Decrement(ref _events);
        return false;
    }

    /// <summary>Gap markers and other bookkeeping events must get through even when the queue is full.</summary>
    public void ForceReserveEvent() => Interlocked.Increment(ref _events);

    public void ReleaseEvent() => Interlocked.Decrement(ref _events);

    public void RecordDrop(DropReason reason, long bytes, long events = 1)
    {
        Interlocked.Add(ref _droppedTotal, events);
        Interlocked.Add(ref _droppedBytesTotal, bytes);
        Interlocked.Add(ref _droppedByReason[(int)reason], events);

        lock (_gapGate)
        {
            var now = DateTime.UtcNow;
            if (_gapEvents == 0) _gapFrom = now;
            _gapTo = now;
            _gapEvents += events;
            _gapBytes += bytes;
            string key = ReasonName(reason);
            _gapReasons[key] = _gapReasons.GetValueOrDefault(key) + events;
        }
    }

    /// <summary>The drops since the last gap marker, or null if there were none; resets the tally.</summary>
    public Gap? TakeGap()
    {
        if (!HasPendingGap) return null;
        lock (_gapGate)
        {
            if (_gapEvents == 0) return null;
            var gap = new Gap(_gapEvents, _gapBytes, _gapFrom, _gapTo, new Dictionary<string, long>(_gapReasons));
            _gapEvents = 0;
            _gapBytes = 0;
            _gapReasons.Clear();
            return gap;
        }
    }

    public static string ReasonName(DropReason reason) => reason switch
    {
        DropReason.QueueFull => "queue_full",
        DropReason.Purged => "purged",
        DropReason.SinkRejected => "sink_rejected",
        _ => "shutdown",
    };
}
