using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>
/// One thing that happened, in the form it is published: a request and how it ended, a session
/// opening or closing, a refused login, or the proxy reporting on itself.
///
/// Event types double as routing keys: request.batch, request.rpc, request.txn, request.bulk,
/// session.start, session.end, session.login_failed, proxy.start, proxy.stop, proxy.heartbeat,
/// proxy.gap and proxy.control. docs/EVENTS.md describes every field.
/// </summary>
internal sealed class AuditEvent
{
    /// <summary>UUIDv7: unique, and ordered by creation time, so consumers can de-duplicate and sort on it.</summary>
    public Guid Id { get; } = Guid.CreateVersion7();

    public required string Type { get; init; }
    public required DateTime TimeUtc { get; init; }
    public SessionSnapshot? Session { get; init; }

    /// <summary>The request's position within its session, counting every request - sampled out or not.</summary>
    public long? Seq { get; init; }

    public double? DurationMs { get; init; }
    public RequestPart? Request { get; init; }
    public ResponsePart? Response { get; init; }

    /// <summary>Fields of the session and proxy events, in the order they are written.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>>? Data { get; init; }

    /// <summary>Events of one session keep their order when this is the partition (or ordering) key.</summary>
    public string PartitionKey => Session is null ? "proxy" : $"s{Session.Id}";
}

internal sealed class RequestPart
{
    /// <summary>batch, rpc, txn or bulk.</summary>
    public required string Kind { get; init; }

    public string? Sql { get; init; }
    public IReadOnlyList<RpcCall>? Calls { get; init; }
    public TransactionRequest? Transaction { get; init; }
    public string? BulkTable { get; init; }

    /// <summary>The transaction the request ran in; zero when none.</summary>
    public ulong? TransactionDescriptor { get; init; }

    /// <summary>A pooled connection was handed to a new user of the pool with this request.</summary>
    public bool ResetConnection { get; init; }

    /// <summary>On a MARS connection, which logical channel (SMP session) carried the request.</summary>
    public ushort? MarsChannel { get; init; }

    /// <summary>A daffytee.* command, answered by DaffyTee itself: the response is DaffyTee's, and the target never saw the request.</summary>
    public string? GatewayCommand { get; init; }

    public long Bytes { get; init; }
    public int Packets { get; init; }

    /// <summary>The request was larger than the per-event cap; SQL, parameters and raw bytes stop short.</summary>
    public bool Truncated { get; init; }

    /// <summary>Set when part of the request could not be decoded; the undecoded part is kept raw.</summary>
    public string? DecodeProblem { get; init; }

    /// <summary>Passwords or other secrets in the SQL or parameters were masked (see SecretRedactor); no raw bytes then.</summary>
    public bool Redacted { get; init; }

    /// <summary>The request payload, exactly as sent - with --raw, or for bulk loads with --bulk full.</summary>
    public byte[]? Raw { get; init; }
}

internal sealed class ResponsePart
{
    /// <summary>ok, error, cancelled (by attention), or incomplete (the session ended first).</summary>
    public required string Status { get; init; }

    public byte DoneToken { get; init; }
    public ushort DoneStatus { get; init; }

    /// <summary>Rows affected or returned by the last statement, when the final DONE carries a count.</summary>
    public ulong? RowCount { get; init; }

    public long Bytes { get; init; }
    public int Packets { get; init; }

    /// <summary>From the end of the request to the first byte of the response: roughly, server think time.</summary>
    public double? FirstByteMs { get; init; }

    /// <summary>Decoded from the whole response, when it was small enough to keep. See ResponseReader.</summary>
    public ResponseDetail? Detail { get; init; }
}
