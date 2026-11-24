using System.Text;

namespace DaffyTee.Sinks;

/// <summary>
/// An append-only text file that starts a new file each day when its name contains {date}
/// (e.g. daffytee-events-{date}.ndjson becomes daffytee-events-20260923.ndjson). Opened for shared
/// reading, so the file can be tailed while DaffyTee writes it. Used for the event file, the SQL log
/// and the operational log alike.
/// </summary>
internal sealed class RollingFileWriter : IDisposable
{
    private readonly string _template;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _currentPath = "";

    public RollingFileWriter(string template)
    {
        _template = Path.GetFullPath(template);
        string? directory = Path.GetDirectoryName(_template);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public string CurrentPath
    {
        get { lock (_gate) return _currentPath.Length > 0 ? _currentPath : Resolve(); }
    }

    public string Template => _template;

    public void Write(string text, bool flush = true)
    {
        lock (_gate)
        {
            var writer = Ensure();
            writer.Write(text);
            if (flush) writer.Flush();
        }
    }

    public void WriteLines(IEnumerable<ReadOnlyMemory<byte>> lines)
    {
        lock (_gate)
        {
            var writer = Ensure();
            writer.Flush();
            var stream = writer.BaseStream;
            foreach (var line in lines)
            {
                stream.Write(line.Span);
                stream.WriteByte((byte)'\n');
            }
            stream.Flush();
        }
    }

    private StreamWriter Ensure()
    {
        string path = Resolve();
        if (_writer is not null && path == _currentPath) return _writer;

        _writer?.Dispose();
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _currentPath = path;
        return _writer;
    }

    private string Resolve() => _template.Replace("{date}", DateTime.Now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
