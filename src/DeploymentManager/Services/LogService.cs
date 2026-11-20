using System.Text;

namespace GenericWeb.DeploymentManager.Services;

public sealed class LogService
{
    private readonly object _sync = new();
    private readonly AppDataService _data;
    // One append writer per active session avoids reopening the file for every
    // stdout/stderr line. AutoFlush keeps every committed line readable on disk.
    private StreamWriter? _sessionWriter;
    public string LogFile { get; private set; }
    public event Action<string>? LineWritten;

    public LogService(AppDataService data)
    {
        _data = data;
        Directory.CreateDirectory(data.Logs);
        LogFile = CreateSessionLogPath();
    }

    public string StartNewSession()
    {
        lock (_sync)
        {
            var next = CreateSessionLogPath();
            // Flush and close the prior session before publishing the next path.
            _sessionWriter?.Dispose();
            _sessionWriter = null;
            LogFile = next;
            return LogFile;
        }
    }

    // Closing the window releases the handle; a late process callback can
    // safely reopen the same session file and still retain its evidence.
    public void CloseSession()
    {
        lock (_sync)
        {
            _sessionWriter?.Dispose();
            _sessionWriter = null;
        }
    }

    // The optional callback only queues already-persisted lines for UI display.
    // It runs under the session lock so disk and UI enqueue preserve the same order.
    public void Write(string message, Action<DateTime, string>? onPersisted = null)
    {
        var clean = SecretRedactor.Redact(message).Trim();
        string line;
        lock (_sync)
        {
            var at = DateTime.Now;
            line = $"[{at:yyyy-MM-dd HH:mm:ss.fff}] {clean}";
            _sessionWriter ??= OpenWriter(LogFile);
            _sessionWriter.WriteLine(line); // AutoFlush before any UI enqueue.
            onPersisted?.Invoke(at, clean);
        }
        // Keep the existing notification outside the I/O lock.
        LineWritten?.Invoke(line);
    }

    private static StreamWriter OpenWriter(string path)
    {
        // Readers (LogForm, diagnostics and the operator) remain supported.
        // External writers cannot interleave unrelated bytes into this session.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 16 * 1024);
        return new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024) { AutoFlush = true };
    }

    private string CreateSessionLogPath()
    {
        Directory.CreateDirectory(_data.Logs);
        while (true)
        {
            var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            var path = Path.Combine(_data.Logs, $"Deploy-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}.log");
            if (!File.Exists(path)) return path;
        }
    }
}
