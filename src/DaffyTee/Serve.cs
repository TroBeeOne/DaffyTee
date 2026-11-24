using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using DaffyTee.Capture;
using DaffyTee.Cli;
using DaffyTee.Commands;
using DaffyTee.Proxy;
using DaffyTee.Repl;
using DaffyTee.Security;
using DaffyTee.Sinks;
using DaffyTee.Tds;

namespace DaffyTee;

/// <summary>`daffytee [serve]`: the proxy itself.</summary>
internal static class Serve
{
    private const int DefaultPort = 1433;
    private const string DefaultEventFile = "daffytee-events-{date}.ndjson";

    public static string Usage => """
          Listening:
            --port N               listen on N (default 1433)
            --bind ADDRESS         127.0.0.1 (default), 0.0.0.0 / any for every IPv4 interface,
                                   :: for IPv4 and IPv6, or one specific address
            --allow-plaintext      also accept clients that will not encrypt (refused by default)
            --cert-file FILE.pfx   present this certificate instead of the generated self-signed one
            --cert-password PW     its password
            --regen-cert           replace the generated certificate
            --machine-scope        (Windows) protect a NEW store's key for the whole machine, for a
                                   service running under another account

          Capture (the tee):
            --sink none|file|rabbit    where events go (default none). One sink at a time.
            --rabbit URI               for --sink rabbit: amqp[s]://user:pw@host:port/vhost. Better kept
                                       out of the command line: DAFFYTEE_RABBIT_URI, or stored
                                       encrypted with 'daffytee config rabbit'
            --rabbit-exchange NAME     topic exchange to publish to (default daffytee)
            --rabbit-queue NAME        also declare a durable queue bound to every event (#) - handy
                                       for testing; without a bound queue RabbitMQ discards events
            --sink-path FILE           for --sink file (default daffytee-events-{date}.ndjson here);
                                       {date} starts a new file each day (quote the name in
                                       PowerShell: "events-{date}.ndjson")
            --sql-log FILE             also write every captured request as runnable T-SQL - the
                                       developer's log ({date} rolls daily, like --sink-path)
            --tee on|off               capture from the start (default: on when a sink or --sql-log is set)
            --sample RATE              capture a fraction: 0.1 or 10% (default 100%)
            --sample-by request|session
            --bulk off|meta|full       bulk loads: nothing, counts only (default), or the raw rows too
            --raw                      add each request's exact bytes to its event (not for requests
                                       with a masked password: the bytes would still hold it)
            --no-redact                keep passwords written into SQL - CREATE LOGIN ... PASSWORD = '...',
                                       CREATE CREDENTIAL ... SECRET = '...' - instead of masking them
            --max-event-kb N           largest request copied into one event (default 1024)
            --response-kb N            responses up to N KB are decoded for errors and counts (default 64)
            --queue-max-mb N           memory the queue may use before dropping (default 256)
            --queue-max-events N       events the queue may hold before dropping (default 100000)
            --instance NAME            name this DaffyTee in events (default host:port)
            --heartbeat SECONDS        proxy.heartbeat interval, 0 for none (default 60)
            --drain SECONDS            at shutdown, wait this long to deliver the queue (default 10)

          Operation:
            --log-file FILE            also write the operational log as JSON lines ({date} rolls daily)
            --no-repl | --repl         no prompt, just listen (automatic when output is redirected)
            --quiet                    log startup and warnings, not every session
            --trace                    print every decoded TDS message, both directions (slow; shows SQL
                                       exactly as sent, passwords included)
            --transcript DIR           one decoded transcript file per session (slow; shows SQL exactly
                                       as sent, passwords included)
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationTokenSource cts)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("try: daffytee --help");
            return 1;
        }

        bool repl = options.Repl ?? (!Console.IsOutputRedirected && !Console.IsInputRedirected);
        Log.Quiet = options.Quiet;
        RollingFileWriter? logFile = options.LogFile is null ? null : LogFile.Attach(options.LogFile);

        var store = UserStore.Open(AppPaths.EnsureHome(), options.MachineScope ? KeyScope.LocalMachine : KeyScope.CurrentUser);

        // Encryption is mandatory unless --allow-plaintext says otherwise, so a certificate is not
        // optional either: without one no client could connect, and failing now beats failing every login.
        X509Certificate2? certificate = null;
        string? certificateProblem = null;
        try
        {
            certificate = ServerCertificate.Load(options.CertificatePath, options.CertificatePassword, options.RegenerateCertificate);
        }
        catch (Exception ex)
        {
            certificateProblem = $"{ex.GetType().Name}: {ex.Message}";
            if (!options.AllowPlaintext)
            {
                Console.Error.WriteLine($"fatal: encryption is mandatory and no certificate could be loaded: {certificateProblem}");
                Console.Error.WriteLine("       fix the certificate (--cert-file / --regen-cert), or run with --allow-plaintext");
                return 2;
            }
        }

        var sessions = new SessionRegistry();
        var context = new ProxyContext
        {
            Users = store,
            Sessions = sessions,
            Certificate = certificate,
            AllowPlaintext = options.AllowPlaintext,
            Trace = options.Trace,
            TranscriptDirectory = options.TranscriptDirectory,
        };

        IEventSink? sink;
        try
        {
            sink = CreateSink(options, store);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"fatal: {ex.Message}");
            return 2;
        }

        await using var pipeline = CreatePipeline(options, sessions, sink);
        Attach(pipeline, context);
        context.Commands = new CommandRunner(context, pipeline, () => Settings(options, store, certificate, pipeline, logFile?.Template));

        var listener = new ProxyListener(options.Address, options.Port, context);
        var listening = new TaskCompletionSource();
        var serving = listener.RunAsync(cts.Token, () => listening.TrySetResult());

        await Task.WhenAny(listening.Task, serving);
        if (serving.IsFaulted) await serving;   // port taken, bad address: surface it as itself

        pipeline.Start();
        TeeCommands.Register(pipeline, interactive: !Console.IsInputRedirected);
        ReplLoop.Extensions.Add(new ReplCommand([":cert"], ":cert [trust|untrust]",
            "the certificate clients see, and whether Windows trusts it",
            (args, output) =>
            {
                new CertCommands(output, certificate, options.CertificatePath).Run(args);
                return Task.CompletedTask;
            }));

        // Whether Windows trusts the certificate is only known, and only matters, for the one
        // DaffyTee generated: a --cert-file certificate is trusted, or not, by its own issuer.
        bool generated = certificate is not null && options.CertificatePath is null;
        bool? trusted = generated && OperatingSystem.IsWindows() ? CertificateTrust.IsTrusted(certificate!) : null;

        StartupBanner.Print(new BannerInfo(
            options.Address, options.Port, store,
            certificate is null ? null : ServerCertificate.PublicPath(options.CertificatePath),
            certificateProblem, options.AllowPlaintext, repl, options.TranscriptDirectory, options.Trace,
            pipeline, logFile?.Template, trusted));

        // Asked at the prompt, once the listener is already up, and only there: a copy started
        // where nobody can answer must not sit waiting for one.
        Action<TextReader, TextWriter>? question = null;
        string certificatePath = ServerCertificate.ResolvePath(null);
        if (generated && CertificateTrust.ShouldOffer(certificate!, certificatePath))
            question = (input, output) => CertificateTrust.Offer(certificate!, certificatePath, input, output);

        try
        {
            if (repl)
            {
                var log = new LogBuffer();
                var console = Console.Out;
                Console.SetOut(log);
                try
                {
                    var loop = new ReplLoop(context, log, console, Console.In, cts, interactive: !Console.IsInputRedirected, question);
                    await loop.RunAsync();
                }
                finally
                {
                    Console.SetOut(console);
                }

                await cts.CancelAsync();
            }

            try { await serving; } catch (OperationCanceledException) { }
        }
        finally
        {
            sessions.AbortAll("DaffyTee is shutting down");

            // Give the sessions' final events a moment to be queued, then deliver what can be.
            await Task.Delay(200);
            await pipeline.StopAsync(TimeSpan.FromSeconds(options.DrainSeconds));
            Log.Info("proxy.stopped", $"stopped; {pipeline.Captured:N0} captured, {pipeline.Published:N0} delivered, {pipeline.Budget.DroppedTotal:N0} dropped");
            logFile?.Dispose();
        }

        return 0;
    }

    private static IEventSink? CreateSink(Options options, UserStore store)
    {
        switch (options.Sink)
        {
            case "file":
                return new NdjsonFileSink(options.SinkPath ?? DefaultEventFile);

            case "rabbit":
            {
                string? uri = options.RabbitUri
                              ?? Environment.GetEnvironmentVariable("DAFFYTEE_RABBIT_URI")
                              ?? store.GetSetting(Cli.ConfigCommands.RabbitUriKey);
                if (string.IsNullOrWhiteSpace(uri))
                    throw new ArgumentException("--sink rabbit needs a URI: --rabbit URI, DAFFYTEE_RABBIT_URI, or 'daffytee config rabbit'");
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("amqp" or "amqps"))
                    throw new ArgumentException("the RabbitMQ URI must be amqp:// or amqps://");
                return new RabbitMqSink(new RabbitSettings(uri, options.RabbitExchange, options.RabbitQueue));
            }

            default:
                return null;
        }
    }

    private static CapturePipeline CreatePipeline(Options options, SessionRegistry sessions, IEventSink? sink)
    {
        var sqlLog = options.SqlLog is null ? null : new RollingFileWriter(options.SqlLog);

        var settings = new TeeSettings
        {
            Enabled = options.Tee ?? (sink is not null || sqlLog is not null),
            SampleRate = options.SampleRate,
            SampleBy = options.SampleBy,
            Bulk = options.Bulk,
            IncludeRaw = options.Raw,
            RedactSecrets = !options.NoRedact,
            MaxEventBytes = options.MaxEventKb * 1024,
            ResponseCaptureBytes = options.ResponseKb * 1024,
            MaxQueueBytes = options.QueueMaxMb * 1024L * 1024,
            MaxQueueEvents = options.QueueMaxEvents,
            Instance = options.Instance ?? $"{Environment.MachineName}:{options.Port}",
        };

        return new CapturePipeline(settings, sink, sqlLog, () => sessions.Active, TimeSpan.FromSeconds(options.HeartbeatSeconds));
    }

    private static void Attach(CapturePipeline pipeline, ProxyContext context)
    {
        context.ObserverFactory = session => [new SessionTap(pipeline, SessionSnapshot.From(session), session.Mars)];

        context.SessionStarted = pipeline.SessionStarted;
        context.SessionEnded = pipeline.SessionEnded;
        context.LoginRefused = pipeline.LoginRefused;
    }

    /// <summary>
    /// daffytee.settings: how this DaffyTee is set up, read when asked, so the capture switches show
    /// their values now. Nothing secret: the RabbitMQ URI is shown as the sink describes itself,
    /// without its password.
    /// </summary>
    private static IReadOnlyList<SettingRow> Settings(Options options, UserStore store, X509Certificate2? certificate,
                                                      CapturePipeline pipeline, string? logFile)
    {
        const string nextStart = "at the next start";
        var tee = pipeline.Settings;
        bool generated = certificate is not null && options.CertificatePath is null;

        var rows = new List<SettingRow>
        {
            new("version", Program.ToolVersion(), "dotnet tool update -g DaffyTee"),
            new("listening", $"{options.Address}:{options.Port}", $"--port, --bind ({nextStart})"),
            new("client_encryption", options.AllowPlaintext ? "optional (--allow-plaintext)" : "mandatory", $"--allow-plaintext ({nextStart})"),
            new("certificate",
                certificate is null ? "none"
                : $"{(generated ? "generated by DaffyTee" : options.CertificatePath)}, expires {certificate.NotAfter:yyyy-MM-dd}, thumbprint {certificate.Thumbprint}",
                $"--cert-file, --regen-cert ({nextStart})"),
        };
        if (generated && OperatingSystem.IsWindows())
            rows.Add(new("certificate_trusted", CertificateTrust.IsTrusted(certificate!) ? "yes, on DaffyTee's machine" : "no",
                         "daffytee cert trust | untrust, on DaffyTee's host"));

        rows.AddRange(
        [
            new("user_store", $"{store.FilePath} ({store.KeyDescription})", $"--config-dir ({nextStart})"),
            new("admins", string.Join(", ", store.Users.Where(u => u.Admin).Select(u => u.Login)),
                "daffytee.set_admin, or daffytee user admin NAME on|off on the host"),
            new("sink", pipeline.Sink?.Description ?? "none", $"--sink, --rabbit, --sink-path ({nextStart})"),
            new("sql_log", pipeline.SqlLog?.Template ?? "none", $"--sql-log ({nextStart})"),
            new("log_file", logFile ?? "none", $"--log-file ({nextStart})"),
            new("tee", tee.Enabled ? "on" : "off", ":tee on|off at DaffyTee's prompt, on the host only"),
            new("sample", $"{tee.SampleRate:P0} of {tee.SampleBy.ToString().ToLowerInvariant()}s",
                "daffytee.set_sample, or :sample at the prompt"),
            new("bulk", tee.Bulk.ToString().ToLowerInvariant(), "daffytee.set_bulk, or :bulk at the prompt"),
            new("raw", tee.IncludeRaw ? "on" : "off", "daffytee.set_raw, or :raw at the prompt"),
            new("redact", tee.RedactSecrets ? "passwords masked" : "passwords NOT masked (--no-redact)", $"--no-redact ({nextStart})"),
            new("max_event_kb", (tee.MaxEventBytes / 1024).ToString(CultureInfo.InvariantCulture), $"--max-event-kb ({nextStart})"),
            new("response_kb", (tee.ResponseCaptureBytes / 1024).ToString(CultureInfo.InvariantCulture), $"--response-kb ({nextStart})"),
            new("queue_max_mb", (tee.MaxQueueBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture), $"--queue-max-mb ({nextStart})"),
            new("queue_max_events", tee.MaxQueueEvents.ToString(CultureInfo.InvariantCulture), $"--queue-max-events ({nextStart})"),
            new("heartbeat_seconds", options.HeartbeatSeconds.ToString(CultureInfo.InvariantCulture), $"--heartbeat ({nextStart})"),
            new("drain_seconds", options.DrainSeconds.ToString(CultureInfo.InvariantCulture), $"--drain ({nextStart})"),
            new("instance", tee.Instance, $"--instance ({nextStart})"),
            new("trace", options.Trace ? "on" : "off", $"--trace ({nextStart})"),
            new("transcript", options.TranscriptDirectory ?? "off", $"--transcript ({nextStart})"),
        ]);
        return rows;
    }

    private sealed record Options(
        int Port, IPAddress Address, bool AllowPlaintext, string? CertificatePath, string? CertificatePassword,
        bool RegenerateCertificate, bool MachineScope, bool? Repl, bool Quiet, bool Trace, string? TranscriptDirectory,
        string Sink, string? SinkPath, string? SqlLog, bool? Tee, double SampleRate, SampleBy SampleBy, BulkMode Bulk,
        bool Raw, int MaxEventKb, int ResponseKb, int QueueMaxMb, int QueueMaxEvents, string? Instance,
        int HeartbeatSeconds, int DrainSeconds, string? LogFile, string? RabbitUri, string RabbitExchange, string? RabbitQueue,
        bool NoRedact)
    {
        public static Options? Parse(IReadOnlyList<string> args)
        {
            int port = DefaultPort;
            IPAddress address = IPAddress.Loopback;
            bool allowPlaintext = false, regenerate = false, machineScope = false, quiet = false, trace = false, raw = false, noRedact = false;
            bool? repl = null, tee = null;
            string? certificatePath = null, certificatePassword = null, transcript = null;
            string sink = "none";
            string? sinkPath = null, sqlLog = null, instance = null, logFile = null, rabbitUri = null, rabbitQueue = null;
            string rabbitExchange = "daffytee";
            double sampleRate = 1.0;
            var sampleBy = SampleBy.Request;
            var bulk = BulkMode.Meta;
            int maxEventKb = 1024, responseKb = 64, queueMaxMb = 256, queueMaxEvents = 100_000, heartbeat = 60, drain = 10;

            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                bool hasValue = i + 1 < args.Count;
                string Next() => args[++i];

                switch (arg)
                {
                    case "--port" when hasValue:
                        if (!TryInt(Next(), 1, 65535, out port)) return Bad(arg, args[i]);
                        break;
                    case "--bind" when hasValue:
                    {
                        string value = Next();
                        IPAddress? parsed = value.ToLowerInvariant() switch
                        {
                            "any" or "0.0.0.0" or "*" => IPAddress.Any,
                            "::" or "[::]" => IPAddress.IPv6Any,
                            "localhost" => IPAddress.Loopback,
                            _ => IPAddress.TryParse(value, out var ip) ? ip : null,
                        };
                        if (parsed is null) return Bad(arg, value);
                        address = parsed;
                        break;
                    }
                    case "--allow-plaintext": allowPlaintext = true; break;
                    case "--cert-file" when hasValue: certificatePath = Next(); break;
                    case "--cert-password" when hasValue: certificatePassword = Next(); break;
                    case "--regen-cert": regenerate = true; break;
                    case "--machine-scope": machineScope = true; break;
                    case "--repl": repl = true; break;
                    case "--no-repl": repl = false; break;
                    case "--quiet" or "-q": quiet = true; break;
                    case "--trace": trace = true; break;
                    case "--transcript" when hasValue: transcript = Path.GetFullPath(Next()); break;

                    case "--sink" when hasValue:
                        sink = Next().ToLowerInvariant();
                        if (sink is not ("none" or "file" or "rabbit" or "rabbitmq")) return Bad(arg, sink);
                        if (sink == "rabbitmq") sink = "rabbit";
                        break;
                    case "--sink-path" when hasValue:
                        if ((sinkPath = FileName(arg, Next())) is null) return null;
                        break;
                    case "--sql-log" when hasValue:
                        if ((sqlLog = FileName(arg, Next())) is null) return null;
                        break;
                    case "--tee" when hasValue:
                    {
                        string value = Next().ToLowerInvariant();
                        if (value is not ("on" or "off")) return Bad(arg, value);
                        tee = value == "on";
                        break;
                    }
                    case "--sample" when hasValue:
                        if (!TeeCommands.TryParseRate(Next(), out sampleRate)) return Bad(arg, args[i]);
                        break;
                    case "--sample-by" when hasValue:
                        if (!Enum.TryParse(Next(), ignoreCase: true, out sampleBy)) return Bad(arg, args[i]);
                        break;
                    case "--bulk" when hasValue:
                        if (!Enum.TryParse(Next(), ignoreCase: true, out bulk)) return Bad(arg, args[i]);
                        break;
                    case "--raw": raw = true; break;
                    case "--no-redact": noRedact = true; break;
                    case "--max-event-kb" when hasValue:
                        if (!TryInt(Next(), 1, 64 * 1024, out maxEventKb)) return Bad(arg, args[i]);
                        break;
                    case "--response-kb" when hasValue:
                        if (!TryInt(Next(), 0, 16 * 1024, out responseKb)) return Bad(arg, args[i]);
                        break;
                    case "--queue-max-mb" when hasValue:
                        if (!TryInt(Next(), 1, 64 * 1024, out queueMaxMb)) return Bad(arg, args[i]);
                        break;
                    case "--queue-max-events" when hasValue:
                        if (!TryInt(Next(), 1, 100_000_000, out queueMaxEvents)) return Bad(arg, args[i]);
                        break;
                    case "--instance" when hasValue: instance = Next(); break;
                    case "--heartbeat" when hasValue:
                        if (!TryInt(Next(), 0, 86_400, out heartbeat)) return Bad(arg, args[i]);
                        break;
                    case "--drain" when hasValue:
                        if (!TryInt(Next(), 0, 3_600, out drain)) return Bad(arg, args[i]);
                        break;
                    case "--log-file" when hasValue:
                        if ((logFile = FileName(arg, Next())) is null) return null;
                        break;
                    case "--rabbit" when hasValue: rabbitUri = Next(); break;
                    case "--rabbit-exchange" when hasValue: rabbitExchange = Next(); break;
                    case "--rabbit-queue" when hasValue: rabbitQueue = Next(); break;

                    default:
                        Console.Error.WriteLine($"unknown or incomplete argument: {arg}");
                        return null;
                }
            }

            if (sinkPath is not null && sink != "file")
                Console.Error.WriteLine("note: --sink-path only applies to --sink file");

            return new Options(port, address, allowPlaintext, certificatePath, certificatePassword, regenerate,
                               machineScope, repl, quiet, trace, transcript, sink, sinkPath, sqlLog, tee, sampleRate,
                               sampleBy, bulk, raw, maxEventKb, responseKb, queueMaxMb, queueMaxEvents, instance,
                               heartbeat, drain, logFile, rabbitUri, rabbitExchange, rabbitQueue, noRedact);
        }

        private static bool TryInt(string text, int min, int max, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

        /// <summary>
        /// PowerShell reads an unquoted {date} as a script block and silently drops it and everything
        /// after it: --sql-log sql-{date}.log arrives here as "sql-", and {date}.log arrives as nothing,
        /// so the option takes the next option as its value. Refuse the second; warn about the first,
        /// whose only tell is the missing extension.
        /// </summary>
        private static string? FileName(string option, string value)
        {
            string hint = $"  in PowerShell, quote names that contain {{date}}: {option} \"name-{{date}}.log\"";
            if (value.StartsWith('-'))
            {
                Console.Error.WriteLine($"{option} needs a file name, not '{value}'");
                Console.Error.WriteLine(hint);
                return null;
            }

            if (Path.GetExtension(value).Length == 0)
            {
                Console.Error.WriteLine($"warning: {option} '{value}' has no file extension - was part of the name lost?");
                Console.Error.WriteLine(hint);
            }

            return value;
        }

        private static Options? Bad(string option, string value)
        {
            Console.Error.WriteLine($"{option}: '{value}' is not valid");
            return null;
        }
    }
}
