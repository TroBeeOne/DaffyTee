using System.Globalization;

namespace DaffyTee.Commands;

/// <summary>
/// One argument of a command. A <paramref name="Secret"/> is masked in events and never logged; a
/// <paramref name="ConnectionString"/> is not logged either, and in events only its password is masked.
/// </summary>
internal sealed record CommandParameter(string Name, string Description, bool Required = true, bool Secret = false,
                                        bool ConnectionString = false);

/// <summary>One daffytee.* command: who may run it, what it takes, and how daffytee.help describes it.</summary>
internal sealed record CommandDefinition(string Name, bool Admin, string Summary, string Example, params CommandParameter[] Parameters)
{
    public string Arguments => string.Join(", ", Parameters.Select(p => p.Required ? $"@{p.Name}" : $"[@{p.Name}]"));

    public string Usage => Parameters.Length == 0 ? $"EXEC daffytee.{Name}" : $"EXEC daffytee.{Name} {Arguments}";
}

/// <summary>
/// Every daffytee.* command. Names put the verb first so they read as sentences, and none is a
/// T-SQL reserved word - daffytee.kill would not even parse - so every command is valid T-SQL that
/// tools accept as it stands. (The pattern, the binding and the help are DaffyDuo's.)
///
/// What is not here is deliberate. Switching the tee off and purging the queue destroy the audit
/// record, so they stay at DaffyTee's own prompt on the host: a leaked admin password must not also
/// be able to switch off the record of what was done with it. Nor is anything that needs a restart
/// or the host itself - where events go, password masking, the certificate.
/// </summary>
internal static class CommandCatalog
{
    private static readonly CommandParameter LoginName = new("login", "a DaffyTee login");

    public static readonly IReadOnlyList<CommandDefinition> All =
    [
        new("help", false, "What you can run here, with examples.", "EXEC daffytee.help 'sessions'",
            new CommandParameter("command", "one command, to see just that one", Required: false)),
        new("status", false, "Your session: its id here, where it is relayed to, its SPID there, and how it is encrypted.",
            "EXEC daffytee.status"),

        new("sessions", true, "Every connected session: who, relayed where, SPID, database, application, traffic, idle time.",
            "EXEC daffytee.sessions"),
        new("kill_session", true,
            "Disconnect a session, by its id in daffytee.sessions (not its SPID). The target rolls back whatever it had open.",
            "EXEC daffytee.kill_session 12", new CommandParameter("session", "the session's id from daffytee.sessions")),
        new("health", true, "How DaffyTee is doing: uptime, sessions, logins, where events go, how full the queue is, what was dropped.",
            "EXEC daffytee.health"),
        new("settings", true, "How this DaffyTee is configured, and where each setting is changed.", "EXEC daffytee.settings"),
        new("recent", true, "The last events captured, oldest first, with their SQL.", "EXEC daffytee.recent 20",
            new CommandParameter("count", "how many (default 20)", Required: false)),
        new("log", true, "DaffyTee's own log: sessions, refused logins, the queue's health, admin commands.", "EXEC daffytee.log 50",
            new CommandParameter("count", "how many lines (default 50)", Required: false)),

        new("set_sample", true,
            "Capture a fraction of requests, or of sessions. It cannot be 0 here: capture is switched off only at DaffyTee's prompt.",
            "EXEC daffytee.set_sample '10%'",
            new CommandParameter("rate", "a fraction or a percentage: 0.1, '10%', '100%'"),
            new CommandParameter("by", "'request' (the default) or 'session'", Required: false)),
        new("set_bulk", true, "What to capture of bulk loads: 'off', 'meta' (counts only, the default) or 'full' (the rows too).",
            "EXEC daffytee.set_bulk 'meta'", new CommandParameter("mode", "off, meta or full")),
        new("set_raw", true, "Add each request's exact bytes (base64) to its event (1), or not (0).", "EXEC daffytee.set_raw 0",
            new CommandParameter("raw", "1 or 0")),

        new("users", true, "Every DaffyTee login: admin or not, enabled or not, its target, and when that was last logged in to.",
            "EXEC daffytee.users"),
        new("add_user", true,
            "Create a DaffyTee login and its target. DaffyTee logs in to the target first, and saves nothing if that fails.",
            "EXEC daffytee.add_user 'bob', 'Pa55word!', 'Server=tcp:sql01,1433;Database=Sales;User Id=svc_app;Password=...;TrustServerCertificate=True'",
            LoginName, new CommandParameter("password", "its DaffyTee password", Secret: true),
            new CommandParameter("target", "the target connection string, its password included", ConnectionString: true)),
        new("set_user_password", true, "Change a login's DaffyTee password. Its target is unchanged.",
            "EXEC daffytee.set_user_password 'bob', '...'", LoginName, new CommandParameter("password", "the new password", Secret: true)),
        new("set_target", true,
            "Change where a login's sessions go. The target's password is kept if the new string has none and names the same login. Logged in to first.",
            "EXEC daffytee.set_target 'bob', 'Server=tcp:sql02,1433;Database=Sales;User Id=svc_app;TrustServerCertificate=True'",
            LoginName, new CommandParameter("target", "the new target connection string", ConnectionString: true)),
        new("set_target_password", true,
            "Change the password (or Entra client secret) DaffyTee logs in to a login's target with. Logged in to first.",
            "EXEC daffytee.set_target_password 'bob', '...'", LoginName,
            new CommandParameter("password", "the target's new password", Secret: true)),
        new("test_user", true, "Log in to a login's target now, and say how it went.", "EXEC daffytee.test_user 'bob'", LoginName),
        new("enable_user", true, "Let a disabled login log in again.", "EXEC daffytee.enable_user 'bob'", LoginName),
        new("disable_user", true, "Stop a login logging in. Its open sessions continue (daffytee.kill_session ends them).",
            "EXEC daffytee.disable_user 'bob'", LoginName),
        new("set_admin", true, "Make a login an admin (1), or not (0).", "EXEC daffytee.set_admin 'alice', 1",
            LoginName, new CommandParameter("admin", "1 or 0")),
        new("remove_user", true, "Remove a login and its target. Its open sessions continue until they disconnect.",
            "EXEC daffytee.remove_user 'bob'", LoginName),
    ];

    public static CommandDefinition? Find(string name) =>
        All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Matches arguments to parameters as T-SQL does for a procedure: by position, or by
    /// @name = value, in any mix.
    /// </summary>
    public static BoundArguments Bind(CommandDefinition definition, IReadOnlyList<CommandArgument> arguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        int position = 0;

        foreach (var argument in arguments)
        {
            CommandParameter parameter;
            if (argument.Name is { } name)
            {
                parameter = definition.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw Usage(definition, $"daffytee.{definition.Name} has no parameter @{name}");
            }
            else
            {
                if (position >= definition.Parameters.Length)
                    throw Usage(definition, $"daffytee.{definition.Name} takes {Count(definition.Parameters.Length)}");
                parameter = definition.Parameters[position++];
            }

            if (values.ContainsKey(parameter.Name)) throw Usage(definition, $"@{parameter.Name} is given twice");
            values[parameter.Name] = argument.Value;
        }

        var missing = definition.Parameters.FirstOrDefault(p => p.Required && (!values.TryGetValue(p.Name, out var v) || v is null));
        if (missing is not null) throw Usage(definition, $"daffytee.{definition.Name} needs @{missing.Name} ({missing.Description})");

        return new BoundArguments(definition, values);
    }

    private static string Count(int n) => n switch { 0 => "no arguments", 1 => "one argument", _ => $"{n} arguments" };

    internal static GatewayException Usage(CommandDefinition definition, string problem) =>
        new(GatewayErrors.BadCommand, $"{problem}. Usage: {definition.Usage}   Example: {definition.Example}");
}

/// <summary>A command's arguments, matched to its parameters, read as the types the command needs.</summary>
internal sealed class BoundArguments(CommandDefinition definition, Dictionary<string, object?> values)
{
    public string Text(string name) =>
        OptionalText(name) is { Length: > 0 } text ? text : throw CommandCatalog.Usage(definition, $"@{name} cannot be empty");

    public string? OptionalText(string name) => values.GetValueOrDefault(name) switch
    {
        null => null,
        string s => s.Trim(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    /// <summary>The value exactly as given, for passwords, where spaces count.</summary>
    public string Secret(string name) =>
        values.GetValueOrDefault(name) is string { Length: > 0 } s ? s : throw CommandCatalog.Usage(definition, $"@{name} cannot be empty");

    public bool Flag(string name, bool defaultValue = false) => values.GetValueOrDefault(name) switch
    {
        null => defaultValue,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        short s => s != 0,
        byte b => b != 0,
        decimal d => d != 0,
        string s when s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on" => true,
        string s when s.Trim().ToLowerInvariant() is "0" or "false" or "no" or "off" => false,
        var other => throw CommandCatalog.Usage(definition, $"@{name} must be 1 or 0, not '{other}'"),
    };

    public int Number(string name, int? defaultValue = null) => values.GetValueOrDefault(name) switch
    {
        null when defaultValue is { } fallback => fallback,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        int i => i,
        short s => s,
        byte b => b,
        decimal d when d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        var other => throw CommandCatalog.Usage(definition, $"@{name} must be a whole number, not '{other}'"),
    };
}
