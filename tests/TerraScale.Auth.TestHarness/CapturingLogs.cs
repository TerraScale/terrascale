using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TerraScale.Auth.Tests;

/// <summary>
/// A logging provider that records each formatted message and exception rendering, exactly what
/// a console or OTLP provider receives. Register it with <c>AddSingleton&lt;ILoggerProvider&gt;</c>.
/// </summary>
public sealed class CapturingLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    /// <summary>Everything logged so far.</summary>
    public string Text => string.Join('\n', this.lines);

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this.lines, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> lines, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lines.Enqueue($"{category} scope {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category} {formatter(state, exception)} {exception}");
    }
}
