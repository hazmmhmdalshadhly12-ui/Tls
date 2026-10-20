using System.Collections.Concurrent;
using HAZEM.Tunnel.Core.Models;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Infrastructure.Logging;

/// <summary>
/// Bounded in-memory ring for the UI Logs page (live tail). Never touches the filesystem,
/// never throws, and renders the same redacted lines the file log shows.
/// </summary>
public sealed class InMemoryLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<UiEvent> _events = new();
    private readonly int _capacity;
    private long _dropped;

    public InMemoryLogProvider(int capacity = 2000) => _capacity = capacity;

    public event Action<UiEvent>? EventAdded;

    public IReadOnlyList<UiEvent> Snapshot() => _events.ToArray();

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public ILogger CreateLogger(string categoryName) => new MemoryLogger(this, categoryName);

    public void Append(string category, LogLevel level, string message, Exception? ex)
    {
        try
        {
            var text = message + (ex is null ? "" : $" | {ex.GetType().Name}: {ex.Message}");
            var e = new UiEvent(DateTimeOffset.Now, LevelTag(level), $"[{category}] {text}");
            _events.Enqueue(e);
            while (_events.Count > _capacity)
            {
                if (_events.TryDequeue(out _)) Interlocked.Increment(ref _dropped);
                else break;
            }
            EventAdded?.Invoke(e);
        }
        catch
        {
            // The UI log must never throw either.
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "-"
    };

    public void Dispose() { }

    private sealed class MemoryLogger : ILogger
    {
        private readonly InMemoryLogProvider _provider;
        private readonly string _category;

        public MemoryLogger(InMemoryLogProvider provider, string category)
        {
            _provider = provider;
            _category = Short(category);
        }

        private static string Short(string category) =>
            category.StartsWith("HAZEM.Tunnel.", StringComparison.Ordinal) ? category["HAZEM.Tunnel.".Length..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try { _provider.Append(_category, logLevel, formatter(state, exception), exception); }
            catch { /* never throw */ }
        }
    }
}
