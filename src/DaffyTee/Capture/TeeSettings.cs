namespace DaffyTee.Capture;

/// <summary>What happens to bulk loads (SqlBulkCopy, bcp, BULK INSERT from a client).</summary>
internal enum BulkMode
{
    /// <summary>Not captured at all. The INSERT BULK statement that announces one still is.</summary>
    Off,

    /// <summary>Counted, not copied: bytes, packets, rows inserted, duration. The default - costs no memory.</summary>
    Meta,

    /// <summary>Also the raw row stream, base64, up to the per-event cap.</summary>
    Full,
}

internal enum SampleBy { Request, Session }

/// <summary>
/// The tee's switches. Everything here can be changed from the prompt while sessions are running;
/// a change takes effect at the next request boundary, never part-way through a message.
/// </summary>
internal sealed class TeeSettings
{
    private volatile bool _enabled;
    private volatile int _samplePerMillion = 1_000_000;
    private volatile int _sampleBy = (int)SampleBy.Request;
    private volatile int _bulk = (int)BulkMode.Meta;

    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>Fraction of requests (or sessions) captured, 0..1. Session and proxy events are never sampled.</summary>
    public double SampleRate
    {
        get => _samplePerMillion / 1_000_000.0;
        set => _samplePerMillion = (int)Math.Round(Math.Clamp(value, 0, 1) * 1_000_000);
    }

    public SampleBy SampleBy { get => (SampleBy)_sampleBy; set => _sampleBy = (int)value; }
    public BulkMode Bulk { get => (BulkMode)_bulk; set => _bulk = (int)value; }

    /// <summary>Largest request payload copied into one event; beyond it the event says truncated.</summary>
    public int MaxEventBytes { get; init; } = 1024 * 1024;

    /// <summary>Add the whole request payload, base64, to each event - for byte-exact replay.</summary>
    public bool IncludeRaw { get; set; }

    /// <summary>Mask passwords and secrets written into SQL (CREATE LOGIN ... PASSWORD = '...'). Off with --no-redact.</summary>
    public bool RedactSecrets { get; init; } = true;

    /// <summary>Responses up to this size are kept whole and decoded for errors, row counts and context changes.</summary>
    public int ResponseCaptureBytes { get; init; } = 64 * 1024;

    public long MaxQueueBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxQueueEvents { get; init; } = 100_000;

    /// <summary>Names this proxy in every event, so events from several proxies can share a queue.</summary>
    public string Instance { get; init; } = Environment.MachineName;

    public bool SampleThisRequest() => _samplePerMillion >= 1_000_000 || Random.Shared.Next(1_000_000) < _samplePerMillion;

    public string Describe() =>
        !Enabled ? "off"
        : $"on, sampling {SampleRate:P0} of {(SampleBy == SampleBy.Request ? "requests" : "sessions")}, " +
          $"bulk {Bulk.ToString().ToLowerInvariant()}{(IncludeRaw ? ", raw payloads" : "")}, " +
          (RedactSecrets ? "passwords masked" : "passwords NOT masked (--no-redact)");
}
