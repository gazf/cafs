using System.Diagnostics;
using System.Text;

namespace Cafs.App.Util;

/// <summary>
/// Redirects Debug.WriteLine, Trace.WriteLine, Console.WriteLine, and Console.Error.WriteLine
/// to cafs-client.log next to the exe. Thread-safe.
/// </summary>
public static class FileLogger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static string? _logPath;

    public static string LogPath => _logPath ?? "(not initialized)";

    public static void Initialize()
    {
        var dir = AppContext.BaseDirectory;
        _logPath = Path.Combine(dir, "cafs-client.log");

        // Append mode; OS-level newline
        var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        var listener = new FileListener();
        Trace.Listeners.Add(listener);

        Console.SetOut(new FileTextWriter(isError: false));
        Console.SetError(new FileTextWriter(isError: true));

        Write($"--- cafs-client started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    internal static void Write(string line)
    {
        lock (_lock)
        {
            _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
        }
    }

    private sealed class FileListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null) FileLogger.Write(message);
        }

        public override void WriteLine(string? message)
        {
            if (message is not null) FileLogger.Write(message);
        }
    }

    private sealed class FileTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly bool _isError;

        public FileTextWriter(bool isError) { _isError = isError; }
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_buffer)
            {
                if (value == '\n')
                {
                    FileLogger.Write((_isError ? "[err] " : "") + _buffer.ToString().TrimEnd('\r'));
                    _buffer.Clear();
                }
                else
                {
                    _buffer.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var ch in value) Write(ch);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }
}
