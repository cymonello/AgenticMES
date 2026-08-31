using System.Text;
using Microsoft.Extensions.Logging;

namespace AgenticMES.ConsoleApp;

/// <summary>
/// Unfiltered file sink: one UTF-8 log file per local calendar day
/// (<c>logs/agentic-mes-yyyy-MM-dd.log</c>). Each write appends and closes so shutdown cannot
/// leave a 0-byte file. Independent of <see cref="DemoLogCapture"/>.
/// </summary>
[ProviderAlias("File")]
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private bool _disposed;

    public DailyFileLoggerProvider(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs"));
        Directory.CreateDirectory(_directory);
        WriteRaw($"# AgenticMES session {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} pid={Environment.ProcessId}");
    }

    public string CurrentFilePath =>
        Path.GetFullPath(Path.Combine(_directory, $"agentic-mes-{DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}.log"));

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level,-12}] {category} {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        WriteRaw(line);
    }

    private void WriteRaw(string line)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                File.AppendAllText(CurrentFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger(string categoryName, DailyFileLoggerProvider sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            sink.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
