using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>
/// Who a session is, frozen when it starts, for every event it produces. TdsVersion is the one the
/// target's LOGINACK settled on: responses are read by it (see <see cref="Tds.TdsConstants.IsPre72"/>).
/// </summary>
internal sealed record SessionSnapshot(
    int Id, Guid ConnectionId, string Login, string TargetLogin, string Target, ushort Spid, string? Database,
    string Host, string App, string Client, string ClientSecurity, string TargetSecurity, string ServerVersion,
    bool ColumnEncryption, string? RoutedTo = null, uint TdsVersion = 0)
{
    public static SessionSnapshot From(Proxy.ProxySession session) => new(
        session.Id, session.ConnectionId, session.Login ?? "", session.TargetLogin ?? "", session.Target ?? "",
        session.Spid, session.Database, session.HostName ?? "", session.AppName ?? "", session.ClientAddress,
        session.FrontSecurity, session.UpstreamSecurity, session.ServerVersion ?? "", session.ColumnEncryption,
        session.RoutedTo, session.TdsVersion);
}

/// <summary>
/// One request and its response, as copied off the relay: the request payload (while it fits), and
/// enough about the response to say how it ended. Decoding into an event happens later, on the
/// pipeline's worker, so the relay only ever pays for a memory copy.
///
/// The payload buffer is rented from the shared pool and its bytes are reserved against the
/// capture budget; <see cref="Release"/> gives both back, and must be called exactly once.
/// </summary>
internal sealed class CapturedExchange
{
    public required SessionSnapshot Session { get; init; }
    public required long Seq { get; init; }
    public required byte Type { get; init; }
    public required bool ResetConnection { get; init; }

    /// <summary>The SMP channel, for a request on a MARS connection.</summary>
    public ushort? MarsChannel { get; init; }

    /// <summary>"daffytee.sessions": the request was a command DaffyTee answered itself; the target never saw it.</summary>
    public string? GatewayCommand { get; init; }
    public required DateTime StartUtc { get; init; }
    public required long StartTimestamp { get; init; }

    /// <summary>Whether this request becomes an event at all (tee on, sampled in, a kind worth reporting).</summary>
    public required bool Emit { get; init; }

    /// <summary>False when the request is tracked (for pairing and counts) but its bytes are not kept.</summary>
    public bool Capturing { get; set; }

    public long RequestEndTimestamp { get; set; }
    public long RequestBytes { get; set; }
    public int RequestPackets { get; set; }
    public bool Truncated { get; set; }

    /// <summary>The copy could not be completed because the budget ran out. Relayed, counted, not sent.</summary>
    public bool Dropped { get; set; }

    public bool Attention { get; set; }

    public long ResponseStartTimestamp { get; set; }
    public long ResponseEndTimestamp { get; set; }
    public long ResponseBytes { get; set; }
    public int ResponsePackets { get; set; }
    public bool Completed { get; set; }

    /// <summary>The response's last token, which is always a DONE, DONEPROC or DONEINPROC.</summary>
    public byte DoneToken { get; set; }
    public ushort DoneStatus { get; set; }
    public ulong DoneRowCount { get; set; }

    /// <summary>The whole response, when it was small enough to keep; decoded on the worker.</summary>
    public byte[]? ResponsePayload { get; set; }
    public int ResponsePayloadLength { get; set; }

    private byte[]? _payload;
    private int _length;
    private int _reserved;

    public ReadOnlySpan<byte> Payload => _payload.AsSpan(0, _length);
    public int ReservedBytes => _reserved;

    public double DurationMs => Completed ? Stopwatch.GetElapsedTime(StartTimestamp, ResponseEndTimestamp).TotalMilliseconds : 0;

    /// <summary>Copies request bytes, growing the buffer inside the per-event cap and the shared budget.</summary>
    public void Append(ReadOnlySpan<byte> data, int maxBytes, CaptureBudget budget)
    {
        if (!Capturing || Dropped) return;

        int room = maxBytes - _length;
        if (room <= 0)
        {
            Truncated = true;
            return;
        }

        if (data.Length > room)
        {
            data = data[..room];
            Truncated = true;
        }

        // Capacity is what was reserved, not the rented array's length: the pool may hand back more.
        int needed = _length + data.Length;
        if (_payload is null || needed > _reserved)
        {
            int size = Math.Min(maxBytes, Math.Max(needed, Math.Max(4096, _reserved * 2)));
            if (!budget.TryReserveBytes(size - _reserved))
            {
                Dropped = true;
                Release(budget);
                return;
            }

            _reserved = size;
            byte[] bigger = ArrayPool<byte>.Shared.Rent(size);
            if (_payload is not null)
            {
                _payload.AsSpan(0, _length).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(_payload);
            }
            _payload = bigger;
        }

        data.CopyTo(_payload.AsSpan(_length));
        _length += data.Length;
    }

    public void Release(CaptureBudget budget)
    {
        if (_payload is not null)
        {
            ArrayPool<byte>.Shared.Return(_payload);
            _payload = null;
        }

        if (ResponsePayload is not null)
        {
            ArrayPool<byte>.Shared.Return(ResponsePayload);
            ResponsePayload = null;
        }

        budget.ReleaseBytes(_reserved);
        _reserved = 0;
        _length = 0;
    }

    /// <summary>Takes over the response buffer and its reservation from the tap.</summary>
    public void AdoptResponse(byte[] buffer, int length, int reserved)
    {
        ResponsePayload = buffer;
        ResponsePayloadLength = length;
        _reserved += reserved;
    }

    /// <summary>
    /// Reads the final DONE from the end of the response: token, status, curcmd, and an 8-byte count -
    /// the last 13 bytes - or, before TDS 7.2, a 4-byte one in the last 9.
    /// </summary>
    public void ReadFinalDone(ReadOnlySpan<byte> tail, uint tdsVersion = 0)
    {
        bool pre72 = TdsConstants.IsPre72(tdsVersion);
        int length = TdsConstants.DoneLength(tdsVersion) + 1;
        if (tail.Length < length) return;
        var done = tail[^length..];
        byte token = done[0];
        if (token is not (TdsConstants.Token.Done or TdsConstants.Token.DoneProc or TdsConstants.Token.DoneInProc)) return;

        DoneToken = token;
        DoneStatus = BinaryPrimitives.ReadUInt16LittleEndian(done[1..]);
        DoneRowCount = pre72 ? BinaryPrimitives.ReadUInt32LittleEndian(done[5..]) : BinaryPrimitives.ReadUInt64LittleEndian(done[5..]);
    }
}
