using System.Collections.Concurrent;

namespace Wiser.Monitor.Services;

public sealed record MonitorEventEntry(
    long AtUnix,
    string Level,
    string Category,
    string Message);

/// <summary>Ring buffer of recent warnings/errors for the Settings diagnostics panel.</summary>
public sealed class MonitorEventLog
{
    private const int MaxEntries = 80;
    private readonly ConcurrentQueue<MonitorEventEntry> _entries = new();

    public void Add(string level, string category, string message)
    {
        var entry = new MonitorEventEntry(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            level,
            category,
            message);
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<MonitorEventEntry> Snapshot() => _entries.Reverse().ToList();
}

public sealed class MonitorEventLogLoggerProvider(MonitorEventLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MonitorEventLogLogger(log, categoryName);

    public void Dispose()
    {
    }

    private sealed class MonitorEventLogLogger(MonitorEventLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var msg = formatter(state, exception);
            if (exception is not null)
                msg = string.IsNullOrWhiteSpace(msg) ? exception.Message : $"{msg} ({exception.Message})";

            if (string.IsNullOrWhiteSpace(msg))
                return;

            var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            log.Add(logLevel.ToString(), shortCategory, msg);
        }
    }
}
