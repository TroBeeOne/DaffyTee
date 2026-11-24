using System.Globalization;
using DaffyTee.Repl;

namespace DaffyTee.Capture;

/// <summary>
/// The prompt's controls for the tee: switch it, sample it, watch it, and see how the queue is doing.
/// Every change is logged and published as a proxy.control event, so a gap in the stream that was
/// somebody's decision can be told apart from one that was not.
/// </summary>
internal static class TeeCommands
{
    public static void Register(CapturePipeline pipeline, bool interactive)
    {
        var settings = pipeline.Settings;

        ReplLoop.Extensions.Add(new ReplCommand([":tee"], ":tee [on|off]", "switch capture on or off (sessions are unaffected)",
            (args, output) =>
            {
                if (args.Length == 1 && args[0].ToLowerInvariant() is "on" or "off")
                {
                    settings.Enabled = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                    Changed(pipeline, $"tee {(settings.Enabled ? "on" : "off")}");
                }
                output.WriteLine($"  tee: {settings.Describe()}");
                return Task.CompletedTask;
            },
            Status: output => WriteStatus(pipeline, output)));

        ReplCommand(":sample", ":sample RATE [request|session]", "capture a fraction, e.g. :sample 10%  (:sample 100% for all)",
            (args, output) =>
            {
                if (args.Length is < 1 or > 2 || !TryParseRate(args[0], out double rate))
                {
                    output.WriteLine("  usage: :sample RATE [request|session]   e.g. :sample 10%   :sample 0.25 session");
                    return;
                }

                if (args.Length == 2)
                {
                    if (!Enum.TryParse<SampleBy>(args[1], ignoreCase: true, out var by))
                    {
                        output.WriteLine("  sample by 'request' or 'session'");
                        return;
                    }
                    settings.SampleBy = by;
                }

                settings.SampleRate = rate;
                Changed(pipeline, $"sampling {rate:P0} of {settings.SampleBy.ToString().ToLowerInvariant()}s");
                output.WriteLine($"  tee: {settings.Describe()}");
            });

        ReplCommand(":bulk", ":bulk off|meta|full", "what to capture of bulk loads",
            (args, output) =>
            {
                if (args.Length != 1 || !Enum.TryParse<BulkMode>(args[0], ignoreCase: true, out var mode))
                {
                    output.WriteLine("  usage: :bulk off|meta|full   (meta: counts only - the default; full: the raw rows too)");
                    return;
                }
                settings.Bulk = mode;
                Changed(pipeline, $"bulk {mode.ToString().ToLowerInvariant()}");
                output.WriteLine($"  tee: {settings.Describe()}");
            });

        ReplCommand(":raw", ":raw on|off", "add each request's exact bytes (base64) to its event",
            (args, output) =>
            {
                if (args.Length != 1 || args[0].ToLowerInvariant() is not ("on" or "off"))
                {
                    output.WriteLine("  usage: :raw on|off");
                    return;
                }
                settings.IncludeRaw = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                Changed(pipeline, $"raw {(settings.IncludeRaw ? "on" : "off")}");
                output.WriteLine($"  tee: {settings.Describe()}");
            });

        ReplCommand(":recent", ":recent [n]", "the last n captured events as SQL (default 10)",
            (args, output) =>
            {
                int count = args.Length > 0 && int.TryParse(args[0], out int n) ? n : 10;
                var events = pipeline.Recent.Tail(count);
                if (events.Count == 0)
                {
                    output.WriteLine(settings.Enabled ? "  nothing captured yet" : "  nothing captured - the tee is off (:tee on)");
                    return;
                }

                output.WriteLine();
                foreach (var e in events) output.Write(Indent(SqlText.Render(e)));
                output.WriteLine();
            });

        ReplCommand(":watch", ":watch", "stream captured requests as SQL, live, until a key is pressed",
            (_, output) =>
            {
                if (!interactive)
                {
                    output.WriteLine("  :watch needs an interactive console");
                    return;
                }

                if (!settings.Enabled) output.WriteLine("  note: the tee is off, so nothing will appear - :tee on");
                output.WriteLine("  watching - press any key to stop");
                pipeline.Recent.Watcher = e => output.Write(Indent(SqlText.Render(e)));
                try { Console.ReadKey(intercept: true); }
                finally { pipeline.Recent.Watcher = null; }
                output.WriteLine("  stopped watching");
            });

        ReplCommand(":purge", ":purge", "drop everything waiting in the queue (reported as a gap)",
            (_, output) =>
            {
                long waiting = pipeline.Budget.Events;
                pipeline.Purge();
                output.WriteLine($"  purging {waiting:N0} queued event(s); consumers will see a proxy.gap with reason 'purged'");
            });
    }

    private static void ReplCommand(string name, string usage, string description, Action<string[], TextWriter> run) =>
        ReplLoop.Extensions.Add(new ReplCommand([name], usage, description, (args, output) =>
        {
            run(args, output);
            return Task.CompletedTask;
        }));

    private static void Changed(CapturePipeline pipeline, string change) => pipeline.Control(change);

    public static bool TryParseRate(string text, out double rate)
    {
        text = text.Trim();
        bool percent = text.EndsWith('%');
        if (percent) text = text[..^1];

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out rate)) return false;
        if (percent || rate > 1) rate /= 100;
        return rate is >= 0 and <= 1;
    }

    private static void WriteStatus(CapturePipeline pipeline, TextWriter output)
    {
        var health = pipeline.Health();
        output.WriteLine($"  tee      : {health.Tee}");
        output.WriteLine($"  outputs  : {health.Outputs}" + (health.SqlLog is null ? "" : $"; SQL log {health.SqlLog}") + "; :watch / :recent");

        if (health.SinkState is { } state)
        {
            if (health.UnavailableSinceUtc is { } since) state += $" since {since.ToLocalTime():HH:mm:ss}";
            if (health.SinkError is { } error) state += $" ({error})";
            output.WriteLine($"  sink     : {state}; last delivery {(health.LastDeliveryUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "never")}");
        }

        output.WriteLine($"  queue    : {health.QueueEvents:N0} event(s), {health.QueueBytes / 1024.0 / 1024:F1} MB " +
                         $"of {health.QueueMaxBytes / 1024 / 1024} MB / {health.QueueMaxEvents:N0} ({health.QueueFill:P0} full)");
        output.WriteLine($"  counts   : {health.Captured:N0} captured, {health.Delivered:N0} delivered, " +
                         $"{health.Dropped:N0} dropped" +
                         (health.Dropped == 0 ? "" :
                             $" (queue full {health.DroppedQueueFull:N0}, purged {health.DroppedPurged:N0}, " +
                             $"rejected {health.DroppedRejected:N0}, shutdown {health.DroppedShutdown:N0})"));
    }

    private static string Indent(string text) => "  " + text.TrimEnd('\n').Replace("\n", "\n  ") + "\n";
}
