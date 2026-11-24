using System.Globalization;
using System.Text;
using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>
/// Renders events as T-SQL a person can read and run: the developer's view of what an application
/// sent. It feeds :watch at the prompt and the --sql-log file.
///
/// Every request becomes a runnable block ending in GO. Parameterised calls are rewritten as
/// sp_executesql with the parameter values as literals; output parameters and table-valued
/// parameters get DECLAREs so the block runs as-is in Management Studio or sqlcmd. What cannot be
/// replayed as text (a bulk load, an encrypted value) is described in a comment instead.
/// </summary>
internal static class SqlText
{
    public static string Render(AuditEvent e) => "-- " + Header(e) + "\n" + Body(e);

    /// <summary>A request as runnable T-SQL ending in GO: <see cref="Render"/> without its header line. Empty for other events.</summary>
    public static string Body(AuditEvent e)
    {
        if (e.Request is not { } request) return "";
        var sb = new StringBuilder();

        if (request.GatewayCommand is not null) sb.Append("-- (answered by DaffyTee itself; the target never saw it)\n");
        if (request.ResetConnection) sb.Append("-- (pooled connection reset before this request)\n");
        if (request.Truncated) sb.Append($"-- (TRUNCATED: {request.Bytes:N0} bytes sent, only the first part was kept)\n");
        if (request.Redacted) sb.Append($"-- (passwords and secrets masked as {SecretRedactor.Mask})\n");
        if (request.DecodeProblem is { } problem) sb.Append("-- (partly undecoded: ").Append(problem).Append(")\n");

        switch (request.Kind)
        {
            case "batch":
                sb.Append(request.Sql?.TrimEnd()).Append('\n');
                break;

            case "rpc":
                foreach (var call in request.Calls ?? []) RenderCall(sb, call);
                break;

            case "txn" when request.Transaction is { } t:
                RenderTransaction(sb, t);
                break;

            case "bulk":
                sb.Append($"-- BULK LOAD into {request.BulkTable ?? "(table not seen)"}: " +
                          $"{e.Response?.RowCount?.ToString("N0") ?? "?"} row(s), {request.Bytes:N0} bytes in {request.Packets} packet(s)\n");
                break;
        }

        if (e.Response?.Detail is { } detail)
        {
            foreach (var error in detail.Errors)
                sb.Append($"-- ERROR {error.Number} (severity {error.Severity}, state {error.State}): {error.Text}\n");
            if (detail.Database is { } database) sb.Append($"-- now in database {database}\n");
            foreach (string transaction in detail.Transactions) sb.Append($"-- transaction {transaction}\n");
            if (detail.OutputParameters.Count > 0)
                sb.Append("-- output: ").Append(string.Join(", ", detail.OutputParameters.Select(p => $"{p.Name} = {Literal(p.Value, "")}"))).Append('\n');
        }

        sb.Append("GO\n");
        return sb.ToString();
    }

    /// <summary>One line: when, who, where, how long, how it ended.</summary>
    public static string Header(AuditEvent e)
    {
        var sb = new StringBuilder();
        sb.Append(e.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ");

        if (e.Session is { } s)
        {
            sb.Append($"s{s.Id}");
            if (e.Seq is { } seq) sb.Append($"#{seq}");
            sb.Append($"  {s.Login} -> {s.TargetLogin}@{s.Target}");
            if (s.Database is not null) sb.Append($"/{s.Database}");
            if (s.App.Length > 0) sb.Append($"  app={s.App}");
        }
        else
        {
            sb.Append("proxy");
        }

        if (e.Request is null)
        {
            if (e.Type == "session.start" && e.Session?.RoutedTo is { } routedTo) sb.Append($"  (redirected to {routedTo})");
            sb.Append("  ").Append(e.Type);
            if (e.Data is { } data)
                sb.Append("  ").Append(string.Join(" ", data.Where(d => d.Value is not null and not IEnumerable<KeyValuePair<string, object?>>)
                                                             .Select(d => $"{d.Key}={Convert.ToString(d.Value, CultureInfo.InvariantCulture)}")));
            return sb.ToString();
        }

        if (e.DurationMs is { } ms) sb.Append($"  {ms:F2} ms");
        if (e.Response is { } r)
        {
            sb.Append("  ").Append(r.Status);
            var counts = r.Detail?.RowCounts;
            if (counts is { Count: > 1 }) sb.Append($"  rows {string.Join("/", counts)}");
            else if (r.RowCount is { } rows) sb.Append($"  {rows} row(s)");
        }
        return sb.ToString();
    }

    private static void RenderCall(StringBuilder sb, RpcCall call)
    {
        var declarations = new StringBuilder();
        var assignments = new List<string>();
        int counter = 0;

        foreach (var p in call.Parameters)
        {
            string value;
            if (p.Value is TableValue table)
            {
                string variable = $"@tvp{++counter}";
                declarations.Append($"DECLARE {variable} {table.Type.TypeName};\n");
                if (table.Rows.Count > 0)
                {
                    declarations.Append($"INSERT {variable} VALUES\n    ");
                    declarations.Append(string.Join(",\n    ", table.Rows.Select(row =>
                        "(" + string.Join(", ", row.Select((cell, i) => Literal(cell, table.Type.Columns[i].Type.SqlName))) + ")")));
                    declarations.Append(";\n");
                }
                value = variable;
            }
            else if (p.IsOutput)
            {
                string variable = $"@out{++counter}";
                declarations.Append($"DECLARE {variable} {p.DeclaredType} = {Literal(p.Value, p.DeclaredType)};\n");
                value = $"{variable} OUTPUT";
            }
            else
            {
                value = Literal(p.Value, p.DeclaredType);
            }

            assignments.Add(p.Name.Length > 0 ? $"{p.Name} = {value}" : value);
        }

        sb.Append(declarations);

        switch (call.ProcId)
        {
            case TdsConstants.ProcId.ExecuteSql or TdsConstants.ProcId.PrepExec or TdsConstants.ProcId.Prepare
                or TdsConstants.ProcId.CursorOpen or TdsConstants.ProcId.CursorPrepExec or TdsConstants.ProcId.CursorPrepare
                when call.Sql is not null:
            {
                if (call.ProcId != TdsConstants.ProcId.ExecuteSql)
                    sb.Append($"-- sent as {call.ProcName}; replayed as the equivalent sp_executesql\n");

                sb.Append("EXEC sp_executesql ").Append(Unicode(call.Sql));
                if (!string.IsNullOrEmpty(call.ParamDeclarations))
                {
                    sb.Append(",\n    ").Append(Unicode(call.ParamDeclarations));
                    foreach (string assignment in assignments) sb.Append(",\n    ").Append(assignment);
                }
                sb.Append(";\n");
                break;
            }

            case TdsConstants.ProcId.Execute or TdsConstants.ProcId.CursorExecute:
                sb.Append($"-- {call.ProcName} of prepared handle {call.Handle}");
                sb.Append(call.Sql is null ? " (prepared before the tee was watching)\n" : ":\n");
                if (call.Sql is not null) sb.Append("--   ").Append(call.Sql.Replace("\n", "\n--   ")).Append('\n');
                sb.Append($"EXEC {call.ProcName} {call.Handle}");
                foreach (string assignment in assignments) sb.Append(", ").Append(assignment);
                sb.Append(";\n");
                break;

            case TdsConstants.ProcId.Unprepare:
                sb.Append($"EXEC sp_unprepare {call.Handle};\n");
                break;

            default:
                sb.Append("EXEC ").Append(call.ProcName);
                if (assignments.Count > 0) sb.Append(' ').Append(string.Join(", ", assignments));
                sb.Append(";\n");
                break;
        }

        if (call.Remainder is { } remainder)
            sb.Append($"-- ({call.RemainderReason}: {remainder.Length} more bytes not shown)\n");
    }

    private static void RenderTransaction(StringBuilder sb, TransactionRequest t)
    {
        string name = t.Name is null ? "" : $" {Identifier(t.Name)}";
        switch (t.Operation)
        {
            case "begin":
                if (t.IsolationLevel is { } isolation and not "unchanged")
                    sb.Append($"SET TRANSACTION ISOLATION LEVEL {isolation.ToUpperInvariant()};\n");
                sb.Append($"BEGIN TRANSACTION{name};\n");
                break;
            case "commit":
                sb.Append("COMMIT TRANSACTION;\n");
                break;
            case "rollback":
                sb.Append($"ROLLBACK TRANSACTION{name};\n");
                break;
            case "save":
                sb.Append($"SAVE TRANSACTION{name};\n");
                break;
            default:
                sb.Append($"-- transaction manager request: {t.Operation}\n");
                break;
        }

        if (t.BeginsNew && t.Operation is "commit" or "rollback")
            sb.Append("BEGIN TRANSACTION;   -- the client asked for a new one straight away\n");
    }

    /// <summary>A value as a T-SQL literal that converts back to the same value under its declared type.</summary>
    public static string Literal(object? value, string declaredType) => value switch
    {
        null => "NULL",
        string s => declaredType.StartsWith("var", StringComparison.OrdinalIgnoreCase)
                    || declaredType.StartsWith("char", StringComparison.OrdinalIgnoreCase)
                    || declaredType.StartsWith("text", StringComparison.OrdinalIgnoreCase)
            ? $"'{s.Replace("'", "''")}'"
            : Unicode(s),
        bool b => b ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dt => $"'{dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)}'",
        DateOnly d => $"'{d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
        TimeSpan t => $"'{t.ToString(@"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture)}'",
        DateTimeOffset o => $"'{o.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)}'",
        Guid g => $"'{g}'",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        EncryptedValue => "NULL /* Always Encrypted value: ciphertext only */",
        RawValue raw => $"NULL /* {raw.Reason}, {raw.Bytes.Length} bytes, not decoded */",
        TableValue table => $"NULL /* {table.Type.TypeName}: {table.Rows.Count} row(s) */",
        _ => $"'{value}'",
    };

    private static string Unicode(string s) => $"N'{s.Replace("'", "''")}'";

    private static string Identifier(string name) => $"[{name.Replace("]", "]]")}]";
}
