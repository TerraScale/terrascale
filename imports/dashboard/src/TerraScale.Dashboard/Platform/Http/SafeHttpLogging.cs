using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Platform.Http;

// Framework formatters/scopes can contain raw targets, cookies, headers and exception messages.
// Keep their events and levels, but pass only operational fields to every configured provider.
public static class SafeHttpLogging
{
    public static string Method(string method) => method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" or "CONNECT" or "TRACE" ? method : "[other]";

    public static void AddSafeHttpLogging(this IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(provider => new Factory(new LoggerFactory(
            provider.GetServices<ILoggerProvider>(), provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
            provider.GetRequiredService<IOptions<LoggerFactoryOptions>>()))));

    private sealed class Factory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(inner.CreateLogger(categoryName),
            categoryName.StartsWith("Microsoft.", StringComparison.Ordinal) || categoryName.StartsWith("System.", StringComparison.Ordinal) ||
            categoryName.StartsWith("ModelContextProtocol.", StringComparison.Ordinal));
        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
        public void Dispose() => inner.Dispose();
    }

    private sealed class Logger(ILogger inner, bool framework) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var safe = Fields(state).Where(field => field.Value is string value &&
                (field.Key == "RequestId" && TerraScaleIds.IsValid(value) ||
                 field.Key is "TraceId" or "SpanId" or "ParentId" && value.Length is 16 or 32 && value.All(char.IsAsciiHexDigit))).ToArray();
            return inner.BeginScope(safe);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (!framework) { inner.Log(logLevel, eventId, state, exception, formatter); return; }
            var safe = new List<KeyValuePair<string, object?>> { new("DiagnosticEvent", eventId.Id) };
            foreach (var field in Fields(state))
            {
                if (field.Key is "StatusCode" or "ElapsedMilliseconds" or "Elapsed" && field.Value is int or long or double or float or decimal)
                    safe.Add(field);
                if (field.Key is "Method" or "HttpMethod" && field.Value is string method)
                    safe.Add(new(field.Key, Method(method)));
            }
            if (exception is not null) safe.Add(new("ErrorType", exception.GetType().Name));
            // Never invoke the original formatter or pass its exception/state to a provider.
            inner.Log(logLevel, eventId, safe, null, (fields, _) => string.Join(" ", fields.Select(field => field.Key + "=" + field.Value)));
        }

        private static IEnumerable<KeyValuePair<string, object?>> Fields<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> fields ? fields : [];
    }
}
