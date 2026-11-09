using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TerraScale.Authentication.Storage;

internal sealed class ConflictLogging(ILoggerFactory loggers) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new Logger(loggers.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => loggers.AddProvider(provider);

    public void Dispose()
    {
    }

    private sealed class Logger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(eventId.Id == CoreEventId.SaveChangesFailed.Id && exception is DbUpdateException failure && AuthData.IsConflict(failure) ? LogLevel.Debug : logLevel,
                eventId, state, exception, formatter);
    }
}
