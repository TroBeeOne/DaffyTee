namespace DaffyTee;

internal enum LogLevel { Debug, Info, Warn, Error }

/// <summary>One operational log line. <see cref="Code"/> is stable, so alerts can key on it.</summary>
internal readonly record struct LogEntry(DateTime TimestampUtc, LogLevel Level, string Code, int Session, string Message);

/// <summary>
/// The proxy's own log - sessions, logins, the queue's health - as opposed to the audit events it
/// captures, which are a separate stream with a separate audience.
///
/// Every line carries a stable event code (session.open, upstream.login_failed, ...) alongside the
/// text a person reads, so the same call can feed the console and a machine-readable NDJSON file.
/// Console output goes through Console.Out on purpose: while the prompt owns the terminal, Console.Out
/// is the log buffer, and :log shows it on demand.
///
/// Passwords and connection strings never go through here.
/// </summary>
internal static class Log
{
    /// <summary>--quiet: drop per-session chatter (Debug) from the console. The file still gets it.</summary>
    public static bool Quiet;

    /// <summary>Also receives every entry, for the NDJSON log file.</summary>
    public static Action<LogEntry>? Sink;

    /// <summary>
    /// The latest entries, Debug included even under --quiet, for daffytee.log. Kept whether or not
    /// a prompt is running, so a DaffyTee started with --no-repl can still be asked what it has done.
    /// </summary>
    public static readonly RecentLog Recent = new();

    public static void Debug(string code, string message, int session = 0) => Write(LogLevel.Debug, code, message, session);
    public static void Info(string code, string message, int session = 0) => Write(LogLevel.Info, code, message, session);
    public static void Warn(string code, string message, int session = 0) => Write(LogLevel.Warn, code, message, session);
    public static void Error(string code, string message, int session = 0) => Write(LogLevel.Error, code, message, session);

    private static void Write(LogLevel level, string code, string message, int session)
    {
        var entry = new LogEntry(DateTime.UtcNow, level, code, session, message);
        Recent.Add(entry);

        if (!(Quiet && level == LogLevel.Debug))
        {
            string marker = level switch { LogLevel.Warn => "warning: ", LogLevel.Error => "error: ", _ => "" };
            string prefix = session > 0 ? $"[s{session}] " : "";
            Console.WriteLine($"{entry.TimestampUtc.ToLocalTime():HH:mm:ss} {prefix}{marker}{message}");
        }

        try { Sink?.Invoke(entry); }
        catch { /* the log must never be the thing that breaks a session */ }
    }
}

/// <summary>The last thousand log entries, oldest first.</summary>
internal sealed class RecentLog(int capacity = 1000)
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly object _gate = new();

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > capacity) _entries.Dequeue();
        }
    }

    public IReadOnlyList<LogEntry> Tail(int count)
    {
        lock (_gate) return _entries.Skip(Math.Max(0, _entries.Count - count)).ToList();
    }
}
