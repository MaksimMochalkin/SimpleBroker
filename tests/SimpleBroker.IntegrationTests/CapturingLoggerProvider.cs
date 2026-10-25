namespace SimpleBroker.IntegrationTests;

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

/// <summary>Keeps the last warnings/errors of the test host so a failing test can print them.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _lines);

    public void Dispose() { }

    private sealed class Capture(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = $"[{logLevel}] {category}: {formatter(state, exception)} {exception}";
            if (text.Length > 1200) text = text[..1200];
            while (lines.Count >= 30) lines.TryDequeue(out _);
            lines.Enqueue(text);
        }
    }
}
