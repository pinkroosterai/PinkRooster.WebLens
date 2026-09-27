using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>Keeps every log line, so tests can see the security signals the module is required to emit.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _lines = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Lines => [.. _lines];

    public bool Has(LogLevel level, string fragment) => _lines.Any(l => l.Level == level && l.Message.Contains(fragment, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
