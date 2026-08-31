using System.Collections.Concurrent;
using AgenticMES.Infrastructure.Ai;
using Microsoft.Extensions.Logging;

namespace AgenticMES.ConsoleApp;

/// <summary>
/// In-process logger sink used by the interview demo to surface AI / MES tool activity
/// while <see cref="MesAgentOrchestrator"/> is running.
/// </summary>
public sealed class DemoLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    private volatile bool _captureEnabled;

    public void BeginCapture()
    {
        while (_entries.TryDequeue(out _))
        {
        }

        _captureEnabled = true;
    }

    public void EndCapture() => _captureEnabled = false;

    public bool TryDequeue(out string line) => _entries.TryDequeue(out line!);

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, this);

    public void Dispose() => EndCapture();

    private void Enqueue(string category, LogLevel level, string message, Exception? exception)
    {
        if (!_captureEnabled || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var shortName = category.AsSpan();
        var lastDot = category.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < category.Length - 1)
        {
            shortName = category.AsSpan(lastDot + 1);
        }

        var suffix = exception is null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})";
        _entries.Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {shortName}{suffix}: {message.Trim()}");
    }

    private sealed class CaptureLogger(string categoryName, DemoLogCapture sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

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

            sink.Enqueue(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
