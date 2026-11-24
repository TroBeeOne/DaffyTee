using System.Globalization;
using DaffyTee.Capture;
using DaffyTee.Proxy;
using DaffyTee.Security;
using DaffyTee.Tds;

namespace DaffyTee.Commands;

/// <summary>One row of daffytee.settings: a setting, its value now, and how it is changed.</summary>
internal sealed record SettingRow(string Setting, string Value, string ChangedWith);

/// <summary>
/// Carries out daffytee.* commands. Everyone can ask for help and for their own session; the rest
/// needs a login that is a DaffyTee admin when the command runs - checked against the store every
/// time, so a change made on the host counts at once.
///
/// It works on the same things the prompt does - the session registry, the capture pipeline, the
/// user store through <see cref="UserAdmin"/> - so a session killed here, or a sampling rate set
/// here, is exactly what :kill or :sample would have done, logged and published the same way, with
/// the login that did it named instead of "the console".
/// </summary>
internal sealed class CommandRunner(
    ProxyContext context,
    CapturePipeline pipeline,
    Func<IReadOnlyList<SettingRow>> settings,
    Func<TargetEndpoint, CancellationToken, Task<TargetCheck>>? checkTarget = null) : IGatewayCommands
{
    private const int MaxRecent = 500;
    private const int MaxLog = 1000;

    private readonly Func<TargetEndpoint, CancellationToken, Task<TargetCheck>> _checkTarget = checkTarget ?? UserAdmin.CheckTargetAsync;

    private UserStore Store => context.Users;

    public async Task<Reply> RunAsync(ProxySession caller, DaffyCommand command, CancellationToken cancellationToken)
    {
        var definition = CommandCatalog.Find(command.Name)
            ?? throw new GatewayException(GatewayErrors.BadCommand, $"there is no command daffytee.{command.Name}. EXEC daffytee.help lists them.");

        string login = caller.Login ?? "";
        bool admin = Store.Find(login) is { Admin: true, Enabled: true };
        if (definition.Admin && !admin)
        {
            Log.Warn("admin.refused", $"'{login}': daffytee.{definition.Name} refused, not an admin", caller.Id);
            throw new GatewayException(GatewayErrors.NeedsAdmin,
                $"daffytee.{definition.Name} needs a DaffyTee admin login. An admin is made on DaffyTee's host: daffytee user admin NAME on");
        }

        var args = CommandCatalog.Bind(definition, command.Arguments);
        if (definition.Admin)
            Log.Info("admin.command", $"'{login}': daffytee.{definition.Name} {Describe(definition, args)}".TrimEnd(), caller.Id);

        var by = new Caller(caller, login);
        return definition.Name switch
        {
            "help" => Help(by, admin, args.OptionalText("command")),
            "status" => Status(by, admin),

            "sessions" => Sessions(by),
            "kill_session" => Kill(by, args.Number("session")),
            "health" => Health(),
            "settings" => Settings(),
            "recent" => Recent(Math.Clamp(args.Number("count", 20), 1, MaxRecent)),
            "log" => LogLines(Math.Clamp(args.Number("count", 50), 1, MaxLog)),

            "set_sample" => SetSample(by, args.Text("rate"), args.OptionalText("by")),
            "set_bulk" => SetBulk(by, args.Text("mode")),
            "set_raw" => SetRaw(by, args.Flag("raw")),

            "users" => Users(),
            "add_user" => await AddUserAsync(args.Text("login"), args.Secret("password"), args.Text("target"), cancellationToken),
            "set_user_password" => SetUserPassword(args.Text("login"), args.Secret("password")),
            "set_target" => await SetTargetAsync(args.Text("login"), args.Text("target"), cancellationToken),
            "set_target_password" => await SetTargetPasswordAsync(args.Text("login"), args.Secret("password"), cancellationToken),
            "test_user" => await TestUserAsync(args.Text("login"), cancellationToken),
            "enable_user" => SetEnabled(by, args.Text("login"), true),
            "disable_user" => SetEnabled(by, args.Text("login"), false),
            "set_admin" => SetAdmin(by, args.Text("login"), args.Flag("admin")),
            "remove_user" => RemoveUser(by, args.Text("login")),

            _ => throw new GatewayException(GatewayErrors.BadCommand, $"daffytee.{definition.Name} is not implemented."),
        };
    }

    /// <summary>Who is asking: the session, and the DaffyTee login it logged in with.</summary>
    private sealed record Caller(ProxySession Session, string Login)
    {
        public bool Is(string login) => string.Equals(login, Login, StringComparison.OrdinalIgnoreCase);
    }

    // ---- everyone -------------------------------------------------------------------------------

    private static Reply Help(Caller caller, bool admin, string? only)
    {
        var commands = CommandCatalog.All.Where(c => admin || !c.Admin).ToList();

        if (only is not null)
        {
            string name = only.StartsWith(CommandParser.Schema + ".", StringComparison.OrdinalIgnoreCase) ? only[(CommandParser.Schema.Length + 1)..] : only;
            var found = CommandCatalog.Find(name)
                ?? throw new GatewayException(GatewayErrors.BadCommand, $"there is no command daffytee.{name}. EXEC daffytee.help lists them.");
            if (found.Admin && !admin)
                throw new GatewayException(GatewayErrors.NeedsAdmin, $"daffytee.{found.Name} needs a DaffyTee admin login.");
            commands = [found];
        }

        var reply = new Reply();
        if (only is null)
        {
            var s = caller.Session;
            reply.Messages.Add($"DaffyTee {Program.ToolVersion()}. You are '{caller.Login}', session {s.Id}, relayed to {s.TargetLogin}@{s.Target} as SPID {s.Spid}.");
            reply.Messages.Add("Run each daffytee command on its own, not in a batch with other SQL. DaffyTee answers them itself; they never reach the target.");
            reply.Messages.Add(admin
                ? "Passwords typed here can stay behind in SSMS's query history. On DaffyTee's host, 'daffytee user passwd NAME' and 'daffytee user target NAME' ask for them at a masked prompt instead."
                : "DaffyTee admins can run more: who is connected, what is captured, and the logins. An admin is made on DaffyTee's host with 'daffytee user admin NAME on'.");
        }

        reply.ResultSets.Add(new ResultSet(
            [Text("command", 128), Text("arguments", 256), Text("what_it_does", 512), Text("example", 512)],
            commands.Select(c => new object?[] { $"daffytee.{c.Name}", c.Arguments, c.Summary, c.Example }).ToList()));
        return reply;
    }

    private static Reply Status(Caller caller, bool admin)
    {
        var s = caller.Session;
        var reply = new Reply();
        reply.Messages.Add($"You are session {s.Id} ('{caller.Login}'), relayed to {s.TargetLogin}@{s.Target} as SPID {s.Spid}.");
        reply.ResultSets.Add(new ResultSet(
            [Int("session"), Text("login", 128), Text("target_login", 256), Text("target", 256), Text("redirected_to", 256),
             Int("spid"), Text("database", 128), Text("application", 256), Text("host", 128), Text("client_address", 64),
             Text("client_encryption", 128), Text("target_encryption", 128), Time("connected_since"), BigInt("requests"),
             Text("admin", 3), Text("daffytee_version", 32)],
            [[s.Id, caller.Login, s.TargetLogin, s.Target, s.RoutedTo, (int)s.Spid, s.Database, s.AppName, s.HostName, s.ClientAddress,
              s.FrontSecurity, s.UpstreamSecurity, s.StartedUtc.ToLocalTime(), s.Requests, YesNo(admin), Program.ToolVersion()]]));
        return reply;
    }

    // ---- what is going on -----------------------------------------------------------------------

    private Reply Sessions(Caller caller)
    {
        var reply = new Reply();
        reply.Messages.Add($"You are session {caller.Session.Id}.");
        reply.ResultSets.Add(new ResultSet(
            [Int("session"), Text("login", 128), Text("target_login", 256), Text("target", 256), Text("redirected_to", 256),
             Int("spid"), Text("database", 128), Text("application", 256), Text("host", 128), Text("client_address", 64),
             Text("client_encryption", 128), Text("target_encryption", 128), Text("mars", 3), Text("state", 16),
             BigInt("requests"), BigInt("bytes_from_client"), BigInt("bytes_to_client"), Time("connected_since"), Int("idle_seconds")],
            context.Sessions.Snapshot().Select(s => new object?[]
            {
                s.Id, s.Login, s.TargetLogin, s.Target, s.RoutedTo, s.Spid == 0 ? null : (int)s.Spid, s.Database, s.AppName, s.HostName,
                s.ClientAddress, s.FrontSecurity, s.UpstreamSecurity, YesNo(s.Mars), s.State.ToString().ToLowerInvariant(),
                s.Requests, s.BytesFromClient, s.BytesFromServer, s.StartedUtc.ToLocalTime(),
                (int)(DateTime.UtcNow - s.LastActivityUtc).TotalSeconds,
            }).ToList()));
        return reply;
    }

    private Reply Kill(Caller caller, int id)
    {
        if (id == caller.Session.Id)
            throw new GatewayException(GatewayErrors.Refused, $"session {id} is your own; disconnect instead.");

        var killed = context.Sessions.Kill(id, $"killed by '{caller.Login}' (session {caller.Session.Id}) with daffytee.kill_session")
            ?? throw new GatewayException(GatewayErrors.NotFound, $"there is no session {id}. EXEC daffytee.sessions lists them (by id, not SPID).");

        var reply = new Reply();
        reply.Messages.Add($"Disconnected session {id} ('{killed.Login}', SPID {killed.Spid}). The target rolls back whatever it had open.");
        return reply;
    }

    private Reply Health()
    {
        var sessions = context.Sessions;
        var capture = pipeline.Health();
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Text("version", 32), Time("started"), BigInt("uptime_seconds"), Int("sessions_active"), BigInt("sessions_accepted"),
             BigInt("logins_succeeded"), BigInt("logins_refused"), BigInt("refused_by_target"),
             Text("tee", 256), Text("outputs", 512), Text("sql_log", 512), Text("sink_state", 32), Time("sink_unavailable_since"),
             Text("sink_error", 1024), Time("last_delivery"), BigInt("queue_events"), Number("queue_mb", 1), Number("queue_percent", 1),
             BigInt("captured"), BigInt("delivered"), BigInt("dropped"), BigInt("dropped_queue_full"), BigInt("dropped_purged"),
             BigInt("dropped_rejected"), BigInt("dropped_shutdown")],
            [[Program.ToolVersion(), sessions.StartedUtc.ToLocalTime(), (long)(DateTime.UtcNow - sessions.StartedUtc).TotalSeconds,
              sessions.Active, sessions.Accepted, sessions.LoginsSucceeded, sessions.LoginsFailed, sessions.UpstreamFailures,
              capture.Tee, capture.Outputs, capture.SqlLog, capture.SinkState, capture.UnavailableSinceUtc?.ToLocalTime(),
              capture.SinkError, capture.LastDeliveryUtc?.ToLocalTime(), capture.QueueEvents,
              Math.Round(capture.QueueBytes / 1024m / 1024m, 1), Math.Round((decimal)capture.QueueFill * 100, 1),
              capture.Captured, capture.Delivered, capture.Dropped, capture.DroppedQueueFull, capture.DroppedPurged,
              capture.DroppedRejected, capture.DroppedShutdown]]));
        return reply;
    }

    private Reply Settings()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Text("setting", 64), Text("value", 1024), Text("changed_with", 512)],
            settings().Select(s => new object?[] { s.Setting, s.Value, s.ChangedWith }).ToList()));
        return reply;
    }

    private Reply Recent(int count)
    {
        var events = pipeline.Recent.Tail(count);
        var reply = new Reply();
        if (events.Count == 0)
            reply.Messages.Add(pipeline.Settings.Enabled ? "Nothing captured yet." : "Nothing captured: the tee is off (:tee on at DaffyTee's prompt).");

        reply.ResultSets.Add(new ResultSet(
            [Time("time"), Text("type", 64), Int("session"), BigInt("seq"), Text("login", 128), Text("database", 128),
             Text("application", 256), Number("duration_ms", 3), Text("status", 16), BigInt("row_count"), Text("error"),
             Text("gateway_command", 128), Text("summary"), Text("sql")],
            events.Select(e => new object?[]
            {
                e.TimeUtc.ToLocalTime(), e.Type, e.Session?.Id, e.Seq, e.Session?.Login, e.Session?.Database, e.Session?.App,
                e.DurationMs is { } ms ? Math.Round((decimal)ms, 3) : null, e.Response?.Status, (long?)e.Response?.RowCount,
                e.Response?.Detail?.Errors.FirstOrDefault() is { } error ? $"Msg {error.Number}, Level {error.Severity}: {error.Text}" : null,
                e.Request?.GatewayCommand, SqlText.Header(e), e.Request is null ? null : SqlText.Body(e),
            }).ToList()));
        return reply;
    }

    private static Reply LogLines(int count)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Time("time"), Text("level", 8), Text("event", 64), Int("session"), Text("message")],
            Log.Recent.Tail(count).Select(e => new object?[]
            {
                e.TimestampUtc.ToLocalTime(), e.Level.ToString().ToLowerInvariant(), e.Code, e.Session == 0 ? null : e.Session, e.Message,
            }).ToList()));
        return reply;
    }

    // ---- what is captured -----------------------------------------------------------------------

    private Reply SetSample(Caller caller, string rateText, string? byText)
    {
        var definition = CommandCatalog.Find("set_sample")!;
        if (!TeeCommands.TryParseRate(rateText, out double rate))
            throw CommandCatalog.Usage(definition, $"'{rateText}' is not a rate: give a fraction (0.1) or a percentage ('10%')");

        // Sampling nothing is the tee switched off by another name, and that is only done on the host.
        if (rate <= 0)
            throw new GatewayException(GatewayErrors.Refused,
                "sampling 0% would switch capture off, which is done only at DaffyTee's prompt on the host (:tee off). The lowest rate here is anything above 0.");

        var by = pipeline.Settings.SampleBy;
        if (byText is not null && (!Enum.TryParse(byText, ignoreCase: true, out by) || !Enum.IsDefined(by)))
            throw CommandCatalog.Usage(definition, $"@by is 'request' or 'session', not '{byText}'");

        pipeline.Settings.SampleBy = by;
        pipeline.Settings.SampleRate = rate;
        return Changed(caller, $"sampling {rate:P0} of {by.ToString().ToLowerInvariant()}s");
    }

    private Reply SetBulk(Caller caller, string modeText)
    {
        if (!Enum.TryParse<BulkMode>(modeText, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
            throw CommandCatalog.Usage(CommandCatalog.Find("set_bulk")!, $"@mode is off, meta or full, not '{modeText}'");

        pipeline.Settings.Bulk = mode;
        return Changed(caller, $"bulk {mode.ToString().ToLowerInvariant()}");
    }

    private Reply SetRaw(Caller caller, bool raw)
    {
        pipeline.Settings.IncludeRaw = raw;
        return Changed(caller, $"raw {(raw ? "on" : "off")}");
    }

    /// <summary>Logged and published as proxy.control, as a change at the prompt is - with the login that made it.</summary>
    private Reply Changed(Caller caller, string change)
    {
        pipeline.Control(change, caller.Login, caller.Session.Id);
        var reply = new Reply();
        reply.Messages.Add($"Capture: {pipeline.Settings.Describe()}.");
        if (!pipeline.Settings.Enabled) reply.Messages.Add("The tee is off, so nothing is captured until it is switched on at DaffyTee's prompt (:tee on).");
        return reply;
    }

    // ---- logins ---------------------------------------------------------------------------------

    private Reply Users()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Text("login", 128), Text("admin", 3), Text("enabled", 3), Text("target", 512), Text("authentication", 32),
             Text("encryption", 128), Time("last_validated"), Text("validation", 256), Time("created"), Time("updated")],
            Store.Users.Select(u =>
            {
                var endpoint = UserAdmin.TryEndpoint(u);
                return new object?[]
                {
                    u.Login, YesNo(u.Admin), YesNo(u.Enabled), endpoint?.Describe() ?? "(unreadable target)",
                    endpoint is null ? null : endpoint.IsEntra ? "Entra application" : "SQL login", endpoint?.DescribeSecurity(),
                    u.LastValidatedUtc?.ToLocalTime(), u.LastValidation, u.CreatedUtc.ToLocalTime(), u.UpdatedUtc.ToLocalTime(),
                };
            }).ToList()));
        return reply;
    }

    private async Task<Reply> AddUserAsync(string login, string password, string target, CancellationToken cancellationToken)
    {
        try
        {
            UserStore.ValidateLogin(login);
        }
        catch (ArgumentException ex)
        {
            throw new GatewayException(GatewayErrors.BadCommand, $"'{login}' cannot be a login: {ex.Message}.");
        }

        if (Store.Find(login) is { } existing)
            throw new GatewayException(GatewayErrors.NotFound,
                $"there is already a login '{existing.Login}'. daffytee.set_target and daffytee.set_user_password change it.");

        var endpoint = ParseTarget(target);
        if (endpoint.Password.Length == 0)
            throw new GatewayException(GatewayErrors.BadCommand,
                $"the target connection string has no {(endpoint.IsEntra ? "client secret" : "password")}, and DaffyTee logs in to the target before saving. " +
                $"Include Password=..., or add the login on DaffyTee's host, which asks for it at a masked prompt: daffytee user add {login}");

        var check = await CheckOrThrowAsync(endpoint, "Nothing was saved.", cancellationToken);
        UserAdmin.Create(Store, login, password, endpoint, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"Created '{login}' -> {endpoint.Describe()}: {check.Detail}. It can log in now.");
        Redirected(reply, check);
        WeakPassword(reply, password);
        return reply;
    }

    private Reply SetUserPassword(string login, string password)
    {
        var user = RequireUser(login);
        UserAdmin.SetPassword(Store, user, password);

        var reply = new Reply();
        reply.Messages.Add($"Changed the DaffyTee password of '{user.Login}'. Its target is unchanged, and sessions already open carry on.");
        WeakPassword(reply, password);
        return reply;
    }

    private async Task<Reply> SetTargetAsync(string login, string target, CancellationToken cancellationToken)
    {
        var user = RequireUser(login);
        var endpoint = UserAdmin.KeepPassword(ParseTarget(target), UserAdmin.TryEndpoint(user));
        if (endpoint.Password.Length == 0)
            throw new GatewayException(GatewayErrors.BadCommand,
                $"the new target connection string has no {(endpoint.IsEntra ? "client secret" : "password")}, and it names a different login from " +
                $"the current target, so the current one cannot be kept. Include Password=..., or change it on DaffyTee's host: daffytee user target {user.Login}");

        var check = await CheckOrThrowAsync(endpoint, "Nothing was changed.", cancellationToken);
        UserAdmin.SetTarget(Store, user, endpoint, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"'{user.Login}' now goes to {endpoint.Describe()}: {check.Detail}.");
        reply.Messages.Add("New sessions go there; sessions already open stay where they are.");
        Redirected(reply, check);
        return reply;
    }

    private async Task<Reply> SetTargetPasswordAsync(string login, string password, CancellationToken cancellationToken)
    {
        var user = RequireUser(login);
        var current = UserAdmin.TryEndpoint(user)
            ?? throw new GatewayException(GatewayErrors.BadCommand, $"the target of '{user.Login}' cannot be read; set a whole new one with daffytee.set_target.");

        var endpoint = current with { Password = password };
        var check = await CheckOrThrowAsync(endpoint, "Nothing was changed.", cancellationToken);
        UserAdmin.SetTarget(Store, user, endpoint, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"Changed the {(endpoint.IsEntra ? "client secret" : "password")} DaffyTee logs in to {endpoint.Describe()} with, for '{user.Login}': {check.Detail}.");
        return reply;
    }

    private async Task<Reply> TestUserAsync(string login, CancellationToken cancellationToken)
    {
        var user = RequireUser(login);
        var endpoint = UserAdmin.TryEndpoint(user)
            ?? throw new GatewayException(GatewayErrors.BadCommand, $"the target of '{user.Login}' cannot be read; set a whole new one with daffytee.set_target.");

        var check = await CheckOrThrowAsync(endpoint, "", cancellationToken);
        UserAdmin.RecordValidation(Store, user, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"'{user.Login}' -> {endpoint.Describe()}: ok - {check.Detail}.");
        Redirected(reply, check);
        return reply;
    }

    private Reply SetEnabled(Caller caller, string login, bool enabled)
    {
        var user = RequireUser(login);
        if (!enabled && caller.Is(user.Login))
            throw new GatewayException(GatewayErrors.Refused, "you cannot disable your own login here. Another admin can, or DaffyTee's host can: daffytee user disable NAME");

        UserAdmin.SetEnabled(Store, user, enabled);
        var reply = new Reply();
        reply.Messages.Add(enabled
            ? $"'{user.Login}' can log in again."
            : $"'{user.Login}' can no longer log in. Sessions already open carry on; daffytee.kill_session ends one.");
        return reply;
    }

    private Reply SetAdmin(Caller caller, string login, bool admin)
    {
        var user = RequireUser(login);
        if (!admin && caller.Is(user.Login))
            throw new GatewayException(GatewayErrors.Refused, "you cannot take away your own admin rights here. Another admin can, or DaffyTee's host can: daffytee user admin NAME off");

        UserAdmin.SetAdmin(Store, user, admin);
        var reply = new Reply();
        reply.Messages.Add(admin ? $"'{user.Login}' is now an admin." : $"'{user.Login}' is no longer an admin.");
        return reply;
    }

    private Reply RemoveUser(Caller caller, string login)
    {
        var user = RequireUser(login);
        if (caller.Is(user.Login))
            throw new GatewayException(GatewayErrors.Refused, "you cannot remove your own login here. Another admin can, or DaffyTee's host can: daffytee user remove NAME");

        Store.Remove(user.Login);
        var reply = new Reply();
        reply.Messages.Add($"Removed '{user.Login}' and its target. Sessions already open carry on until they disconnect; daffytee.kill_session ends one.");
        return reply;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private UserRecord RequireUser(string login) =>
        Store.Find(login) ?? throw new GatewayException(GatewayErrors.NotFound, $"there is no login '{login}'. EXEC daffytee.users lists them.");

    private static TargetEndpoint ParseTarget(string connectionString)
    {
        try
        {
            return TargetEndpoint.Parse(connectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new GatewayException(GatewayErrors.BadCommand, $"the target connection string cannot be used: {ex.Message}.");
        }
    }

    /// <summary>Logs in to the target the way the relay would; a failure is the client's error, and nothing is saved.</summary>
    private async Task<TargetCheck> CheckOrThrowAsync(TargetEndpoint endpoint, string consequence, CancellationToken cancellationToken)
    {
        var check = await _checkTarget(endpoint, cancellationToken);
        if (check.Ok) return check;

        string message = $"could not log in to the target {endpoint.Describe()} ({check.Failure}): {check.Problem}";
        if (check.Resuming) message += " (An Azure SQL database that paused itself says this while it resumes, which takes about a minute: try again.)";
        throw new GatewayException(GatewayErrors.TargetFailed, $"{message.TrimEnd('.')}. {consequence}".TrimEnd());
    }

    private static void Redirected(Reply reply, TargetCheck check)
    {
        if (check.RoutedTo is { } routedTo)
            reply.Messages.Add($"The target redirected the login to {routedTo}; DaffyTee follows the redirect itself.");
    }

    private static void WeakPassword(Reply reply, string password)
    {
        if (password.Length < 8) reply.Messages.Add("Note: that password is shorter than 8 characters.");
    }

    /// <summary>What an admin command was given, for the log - with passwords and connection strings left out.</summary>
    private static string Describe(CommandDefinition definition, BoundArguments args) =>
        string.Join(", ", definition.Parameters
            .Where(p => !p.Secret && !p.ConnectionString)
            .Select(p => args.OptionalText(p.Name) is { } value ? $"@{p.Name}='{value}'" : null)
            .Where(s => s is not null));

    private static ColumnDef Text(string name, int? maxChars = null) =>
        new(name, SqlColumnType.NVarChar, MaxLength: maxChars * 2);

    private static ColumnDef Int(string name) => new(name, SqlColumnType.Int);

    private static ColumnDef BigInt(string name) => new(name, SqlColumnType.BigInt);

    private static ColumnDef Time(string name) => new(name, SqlColumnType.DateTime2);

    private static ColumnDef Number(string name, byte scale) => new(name, SqlColumnType.Decimal, Precision: 18, Scale: scale);

    private static string YesNo(bool value) => value ? "yes" : "no";
}
