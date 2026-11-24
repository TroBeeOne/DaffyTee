using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>
/// Writes an <see cref="AuditEvent"/> as a CloudEvents 1.0 JSON document - the envelope most event
/// tooling already understands - with DaffyTee's own schema, versioned by "v", inside "data".
///
/// Written by hand with Utf8JsonWriter rather than by reflection: it is on the path every event
/// takes, and the published schema is a contract that should not shift because a property was
/// renamed in code. docs/EVENTS.md documents it field by field.
///
/// Values keep their SQL meaning: numbers stay numbers (decimals exactly), date and time values
/// are ISO 8601 text, binary is base64, and a table-valued parameter is its columns and rows.
/// </summary>
internal static class EventJson
{
    public const int SchemaVersion = 1;

    private static readonly JsonWriterOptions Options = new()
    {
        // SQL is full of < > & and ' - escaping them to < serves HTML embedding, not a queue.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(AuditEvent e, string source)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteString("specversion", "1.0");
            w.WriteString("id", e.Id);
            w.WriteString("source", source);
            w.WriteString("type", e.Type);
            w.WriteString("time", DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc));
            w.WriteString("datacontenttype", "application/json");
            w.WriteString("partitionkey", e.PartitionKey);

            w.WriteStartObject("data");
            w.WriteNumber("v", SchemaVersion);
            if (e.Seq is { } seq) w.WriteNumber("seq", seq);
            if (e.DurationMs is { } duration) w.WriteNumber("durationMs", duration);
            if (e.Session is { } session) WriteSession(w, session);
            if (e.Request is { } request) WriteRequest(w, request);
            if (e.Response is { } response) WriteResponse(w, response);
            if (e.Data is { } data)
            {
                foreach (var (key, value) in data)
                {
                    w.WritePropertyName(key);
                    WriteValue(w, value);
                }
            }
            w.WriteEndObject();

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteSession(Utf8JsonWriter w, SessionSnapshot s)
    {
        w.WriteStartObject("session");
        w.WriteNumber("id", s.Id);
        w.WriteString("connectionId", s.ConnectionId);
        w.WriteString("login", s.Login);
        w.WriteString("targetLogin", s.TargetLogin);
        w.WriteString("target", s.Target);
        WriteOptional(w, "routedTo", s.RoutedTo);
        w.WriteNumber("spid", s.Spid);
        WriteOptional(w, "database", s.Database);
        w.WriteString("host", s.Host);
        w.WriteString("app", s.App);
        w.WriteString("client", s.Client);
        w.WriteEndObject();
    }

    private static void WriteRequest(Utf8JsonWriter w, RequestPart r)
    {
        w.WriteStartObject("request");
        w.WriteString("kind", r.Kind);
        WriteOptional(w, "gatewayCommand", r.GatewayCommand);
        if (r.TransactionDescriptor is { } txn) w.WriteString("txn", $"0x{txn:X16}");
        if (r.ResetConnection) w.WriteBoolean("resetConnection", true);
        if (r.MarsChannel is { } channel) w.WriteNumber("marsChannel", channel);
        w.WriteNumber("bytes", r.Bytes);
        w.WriteNumber("packets", r.Packets);
        if (r.Truncated) w.WriteBoolean("truncated", true);
        if (r.Redacted) w.WriteBoolean("redacted", true);
        WriteOptional(w, "decodeProblem", r.DecodeProblem);
        WriteOptional(w, "sql", r.Sql);

        if (r.Calls is { } calls)
        {
            w.WriteStartArray("calls");
            foreach (var call in calls) WriteCall(w, call);
            w.WriteEndArray();
        }

        if (r.Transaction is { } t)
        {
            w.WriteStartObject("transaction");
            w.WriteString("op", t.Operation);
            WriteOptional(w, "name", t.Name);
            WriteOptional(w, "isolation", t.IsolationLevel);
            if (t.BeginsNew) w.WriteBoolean("beginsNew", true);
            w.WriteEndObject();
        }

        if (r.Kind == "bulk")
        {
            w.WriteStartObject("bulk");
            WriteOptional(w, "table", r.BulkTable);
            w.WriteEndObject();
        }

        if (r.Raw is { } raw) w.WriteBase64String("raw", raw);
        w.WriteEndObject();
    }

    private static void WriteCall(Utf8JsonWriter w, RpcCall call)
    {
        w.WriteStartObject();
        w.WriteString("proc", call.ProcName);
        if (call.ProcId != 0) w.WriteNumber("procId", call.ProcId);
        WriteOptional(w, "sql", call.Sql);
        WriteOptional(w, "paramDecl", call.ParamDeclarations);
        if (call.Handle is { } handle) w.WriteNumber("handle", handle);

        w.WriteStartArray("params");
        foreach (var p in call.Parameters)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("type", p.DeclaredType);
            w.WriteString("tds", $"0x{p.Type.Type:X2}");
            w.WriteString("dir", p.IsOutput ? "out" : "in");
            if (p.IsDefault) w.WriteBoolean("default", true);
            if (p.IsEncrypted) w.WriteBoolean("encrypted", true);
            w.WritePropertyName("value");
            WriteValue(w, p.Value);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (call.Remainder is { } remainder)
        {
            w.WriteString("undecoded", call.RemainderReason);
            w.WriteBase64String("undecodedBytes", remainder);
        }
        w.WriteEndObject();
    }

    private static void WriteResponse(Utf8JsonWriter w, ResponsePart r)
    {
        w.WriteStartObject("response");
        w.WriteString("status", r.Status);
        if (r.RowCount is { } rows) w.WriteNumber("rowCount", rows);
        if (r.DoneToken != 0)
        {
            w.WriteString("done", TdsConstants.TokenName(r.DoneToken));
            w.WriteString("doneStatus", $"0x{r.DoneStatus:X4}");
        }
        w.WriteNumber("bytes", r.Bytes);
        w.WriteNumber("packets", r.Packets);
        if (r.FirstByteMs is { } firstByte) w.WriteNumber("firstByteMs", firstByte);

        if (r.Detail is { } d)
        {
            if (d.Errors.Count > 0) WriteMessages(w, "errors", d.Errors);
            if (d.Messages.Count > 0) WriteMessages(w, "messages", d.Messages);
            if (d.RowCounts.Count > 0)
            {
                w.WriteStartArray("rowCounts");
                foreach (ulong count in d.RowCounts) w.WriteNumberValue(count);
                w.WriteEndArray();
            }
            if (d.ResultSetRows.Count > 0)
            {
                w.WriteStartArray("resultSets");
                foreach (long count in d.ResultSetRows) w.WriteNumberValue(count);
                w.WriteEndArray();
            }
            if (d.ReturnStatus is { } status) w.WriteNumber("returnStatus", status);
            if (d.OutputParameters.Count > 0)
            {
                w.WriteStartObject("output");
                foreach (var (name, value) in d.OutputParameters)
                {
                    w.WritePropertyName(name);
                    WriteValue(w, value);
                }
                w.WriteEndObject();
            }
            WriteOptional(w, "database", d.Database);
            if (d.Transactions.Count > 0)
            {
                w.WriteStartArray("transactions");
                foreach (string t in d.Transactions) w.WriteStringValue(t);
                w.WriteEndArray();
            }
            WriteOptional(w, "detailProblem", d.Problem);
        }
        w.WriteEndObject();
    }

    private static void WriteMessages(Utf8JsonWriter w, string name, List<ServerMessage> messages)
    {
        w.WriteStartArray(name);
        foreach (var m in messages)
        {
            w.WriteStartObject();
            w.WriteNumber("number", m.Number);
            w.WriteNumber("severity", m.Severity);
            w.WriteNumber("state", m.State);
            w.WriteString("message", m.Text);
            if (m.Procedure.Length > 0) w.WriteString("procedure", m.Procedure);
            w.WriteNumber("line", m.Line);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null) w.WriteString(name, value);
    }

    public static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case byte n: w.WriteNumberValue(n); break;
            case short n: w.WriteNumberValue(n); break;
            case int n: w.WriteNumberValue(n); break;
            case long n: w.WriteNumberValue(n); break;
            case ulong n: w.WriteNumberValue(n); break;
            case float f: w.WriteNumberValue(f); break;
            case double d: w.WriteNumberValue(d); break;
            case decimal m: w.WriteNumberValue(m); break;
            case string s: w.WriteStringValue(s); break;
            case Guid g: w.WriteStringValue(g); break;
            case byte[] bytes: w.WriteBase64StringValue(bytes); break;
            case DateTime dt: w.WriteStringValue(dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)); break;
            case DateOnly d: w.WriteStringValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case TimeSpan t: w.WriteStringValue(t.ToString(@"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset o: w.WriteStringValue(o.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)); break;
            case EncryptedValue e:
                w.WriteStartObject();
                w.WriteBase64String("ciphertext", e.Ciphertext);
                w.WriteEndObject();
                break;
            case RawValue r:
                w.WriteStartObject();
                w.WriteString("undecoded", r.Reason);
                w.WriteBase64String("bytes", r.Bytes);
                w.WriteEndObject();
                break;
            case TableValue t:
                w.WriteStartObject();
                w.WriteString("tableType", t.Type.TypeName);
                w.WriteStartArray("columns");
                foreach (var (name, type) in t.Type.Columns)
                {
                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("type", type.SqlName);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("rows");
                foreach (var row in t.Rows)
                {
                    w.WriteStartArray();
                    foreach (var cell in row) WriteValue(w, cell);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                w.WriteStartObject();
                foreach (var (key, item) in pairs)
                {
                    w.WritePropertyName(key);
                    WriteValue(w, item);
                }
                w.WriteEndObject();
                break;
            default:
                w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
