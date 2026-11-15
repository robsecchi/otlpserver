using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>Collects everything the app logs, so tests can assert on warnings.</summary>
public sealed class CapturingLoggerProvider(ConcurrentQueue<(LogLevel Level, string Message)> logs) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(logs);
    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<(LogLevel, string)> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Enqueue((logLevel, formatter(state, exception)));
    }
}
