using System.Diagnostics;
using System.Text.RegularExpressions;
using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>
/// What the worker remembers about a session between its requests, to make each event
/// self-explanatory: the table an INSERT BULK announced (the bulk rows that follow do not name it),
/// the statements behind prepared handles, and the current database.
/// </summary>
internal sealed class SessionDecodeState
{
    public string? PendingBulkTable { get; set; }
    public Dictionary<int, string> PreparedStatements { get; } = [];
    public string? Database { get; set; }
}

/// <summary>
/// Turns a captured exchange into an <see cref="AuditEvent"/>. Runs on the pipeline's worker, never
/// on the relay, so it can take its time - and it never throws: whatever cannot be decoded is kept
/// raw and flagged, because an event with an undecoded part is still worth far more than no event.
/// </summary>
internal static partial class EventDecoder
{
    [GeneratedRegex(@"^\s*insert\s+bulk\s+((?:\[[^\]]+\]|[^\s(\[.]+)(?:\s*\.\s*(?:\[[^\]]+\]|[^\s(\[.]+)){0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex InsertBulk();

    public static AuditEvent Decode(CapturedExchange exchange, SessionDecodeState state, bool includeRaw, bool redact = true)
    {
        var payload = exchange.Payload;
        string kind = exchange.Type switch
        {
            TdsConstants.PacketType.SqlBatch => "batch",
            TdsConstants.PacketType.Rpc => "rpc",
            TdsConstants.PacketType.TransactionManager => "txn",
            TdsConstants.PacketType.BulkLoad => "bulk",
            _ => $"type{exchange.Type:X2}",
        };

        string? sql = null, problem = null, bulkTable = null;
        IReadOnlyList<RpcCall>? calls = null;
        TransactionRequest? transaction = null;
        ulong? descriptor = null;

        try
        {
            if (exchange.Type != TdsConstants.PacketType.BulkLoad && payload.Length > 0)
                descriptor = AllHeaders.TransactionDescriptor(payload);

            switch (exchange.Type)
            {
                case TdsConstants.PacketType.SqlBatch:
                    sql = RequestParser.ParseBatchText(payload);
                    if (InsertBulk().Match(sql) is { Success: true } match) state.PendingBulkTable = match.Groups[1].Value;
                    break;

                case TdsConstants.PacketType.Rpc:
                    calls = RequestParser.ParseRpc(payload);
                    problem = calls.FirstOrDefault(c => c.Remainder is not null)?.RemainderReason;
                    ForgetUnprepared(calls, state);

                    // An sp_execute carries only a handle; name the statement it runs, if it was prepared in view.
                    calls = calls.Select(c => c.Sql is null && PreparedStatement(c, state) is { } prepared ? c with { Sql = prepared } : c).ToList();
                    break;

                case TdsConstants.PacketType.TransactionManager:
                    transaction = TransactionRequestParser.Parse(payload);
                    break;

                case TdsConstants.PacketType.BulkLoad:
                    bulkTable = state.PendingBulkTable;
                    state.PendingBulkTable = null;
                    break;
            }
        }
        catch (Exception ex)
        {
            // Truncation is the usual cause: a request cut off at the per-event cap ends mid-value.
            problem = exchange.Truncated ? "truncated before it could be fully decoded" : $"{ex.GetType().Name}: {ex.Message}";
        }

        // Before anything is remembered for later requests, so a prepared statement is kept masked too.
        var maskedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool redacted = redact && SecretRedactor.Redact(ref sql, ref calls, maskedParameters);

        ResponseDetail? detail = null;
        if (exchange.ResponsePayload is not null)
        {
            detail = ResponseReader.Read(exchange.ResponsePayload.AsSpan(0, exchange.ResponsePayloadLength), exchange.Session);
            if (redact) redacted |= SecretRedactor.RedactOutputs(detail, maskedParameters);
            if (detail.Database is not null) state.Database = detail.Database;
            ResolvePrepared(calls, detail, state);
        }

        bool includePayload = includeRaw || (exchange.Type == TdsConstants.PacketType.BulkLoad && exchange.Capturing);

        return new AuditEvent
        {
            Type = "request." + kind,
            TimeUtc = exchange.StartUtc,
            Session = state.Database is { } database && database != exchange.Session.Database
                ? exchange.Session with { Database = database }
                : exchange.Session,
            Seq = exchange.Seq,
            DurationMs = exchange.Completed ? Math.Round(exchange.DurationMs, 3) : null,
            Request = new RequestPart
            {
                Kind = kind,
                Sql = sql,
                Calls = calls,
                Transaction = transaction,
                BulkTable = bulkTable,
                TransactionDescriptor = descriptor,
                ResetConnection = exchange.ResetConnection,
                MarsChannel = exchange.MarsChannel,
                GatewayCommand = exchange.GatewayCommand,
                Bytes = exchange.RequestBytes,
                Packets = exchange.RequestPackets,
                Truncated = exchange.Truncated,
                DecodeProblem = problem,
                Redacted = redacted,
                // The exact bytes would still hold what was masked.
                Raw = includePayload && !redacted && payload.Length > 0 ? payload.ToArray() : null,
            },
            Response = new ResponsePart
            {
                Status = !exchange.Completed ? "incomplete"
                       : exchange.Attention ? "cancelled"
                       : (exchange.DoneStatus & (TdsConstants.DoneStatus.Error | TdsConstants.DoneStatus.ServerError)) != 0 || detail?.Errors.Count > 0 ? "error"
                       : "ok",
                DoneToken = exchange.DoneToken,
                DoneStatus = exchange.DoneStatus,
                RowCount = (exchange.DoneStatus & TdsConstants.DoneStatus.Count) != 0 ? exchange.DoneRowCount : null,
                Bytes = exchange.ResponseBytes,
                Packets = exchange.ResponsePackets,
                FirstByteMs = exchange.Completed && exchange.ResponseStartTimestamp >= exchange.RequestEndTimestamp && exchange.RequestEndTimestamp != 0
                    ? Math.Round(Stopwatch.GetElapsedTime(exchange.RequestEndTimestamp, exchange.ResponseStartTimestamp).TotalMilliseconds, 3)
                    : null,
                Detail = detail,
            },
        };
    }

    private static void ForgetUnprepared(IReadOnlyList<RpcCall> calls, SessionDecodeState state)
    {
        foreach (var call in calls)
        {
            if (call.ProcId == TdsConstants.ProcId.Unprepare && call.Handle is int handle)
                state.PreparedStatements.Remove(handle);
        }
    }

    /// <summary>
    /// Pairs the handle a prepare returned (an OUTPUT parameter, read from the response) with the
    /// statement the request carried, so a later sp_execute can say what it ran.
    /// </summary>
    private static void ResolvePrepared(IReadOnlyList<RpcCall>? calls, ResponseDetail detail, SessionDecodeState state)
    {
        if (calls is null) return;

        foreach (var call in calls)
        {
            if (call.ProcId is TdsConstants.ProcId.PrepExec or TdsConstants.ProcId.Prepare
                    or TdsConstants.ProcId.CursorPrepExec or TdsConstants.ProcId.CursorPrepare
                && call.Sql is not null
                && detail.OutputHandle is int handle)
            {
                state.PreparedStatements[handle] = call.Sql;
            }
        }
    }

    /// <summary>The statement behind an sp_execute handle, if this session prepared it while the tee was watching.</summary>
    public static string? PreparedStatement(RpcCall call, SessionDecodeState state) =>
        call.ProcId is TdsConstants.ProcId.Execute or TdsConstants.ProcId.CursorExecute && call.Handle is int handle
            ? state.PreparedStatements.GetValueOrDefault(handle)
            : null;
}
