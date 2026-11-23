using System.Text;
using Cysharp.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZLogger;

namespace TerraScale.Authentication.Hosting;

public sealed class ScrubbedException : Exception
{
    private readonly string? originalStackTrace;
    private readonly string rendered;

    private const int MaxDepth = 16;

    public ScrubbedException(Exception original)
        : this(original, 0)
    {
    }

    private ScrubbedException(Exception original, int depth)
        : base(Describe(original ?? throw new ArgumentNullException(nameof(original))), Inner(original, depth))
    {
        this.OriginalType = original is ScrubbedException scrubbed ? scrubbed.OriginalType : original.GetType().FullName ?? original.GetType().Name;
        this.originalStackTrace = original is ScrubbedException wrapped ? wrapped.originalStackTrace : FramesOf(original);
        this.rendered = original is ScrubbedException already ? already.rendered : Render(original, 0);
    }

    public ScrubbedException()
        : this(new InvalidOperationException())
    {
    }

    public ScrubbedException(string message)
        : this(new InvalidOperationException(message))
    {
    }

    public ScrubbedException(string message, Exception innerException)
        : this(new InvalidOperationException(message, innerException))
    {
    }

    public string OriginalType { get; } = string.Empty;

    public override string? StackTrace => this.originalStackTrace;

    public override string ToString() => this.rendered;

    private static string? FramesOf(Exception exception)
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: true);
            return trace.FrameCount == 0 ? null : trace.ToString().TrimEnd();
        }
        catch (Exception unreadable) when (unreadable is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static Exception? Inner(Exception original, int depth) =>
        original is ScrubbedException ? original.InnerException
        : original.InnerException is { } inner && depth < MaxDepth ? new ScrubbedException(inner, depth + 1)
        : null;

    private static string Describe(Exception exception) =>
        exception is ScrubbedException ? exception.Message : $"{exception.GetType().FullName} (message withheld)";

    private const int MaxRendered = 64;

    private static string Render(Exception root, int depth)
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            var visits = 0;
            Append(ref builder, root, depth, seen, ref visits);
            return builder.ToString().TrimEnd();
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static void Append(ref Utf16ValueStringBuilder builder, Exception exception, int depth, HashSet<Exception> seen, ref int visits)
    {
        if (++visits > MaxRendered || depth > MaxDepth)
        {
            builder.AppendLine("(cause chain truncated)");
            return;
        }

        if (!seen.Add(exception))
        {
            builder.AppendLine($"{exception.GetType().FullName} (repeated)");
            return;
        }

        builder.AppendLine(Describe(exception));
        if ((exception is ScrubbedException wrapped ? wrapped.originalStackTrace : FramesOf(exception)) is { Length: > 0 } trace)
        {
            builder.AppendLine(trace);
        }

        IEnumerable<Exception> causes = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } inner ? [inner] : [];
        foreach (var cause in causes)
        {
            if (visits >= MaxRendered)
            {
                builder.AppendLine(" ---> (further causes truncated)");
                break;
            }

            builder.Append(" ---> ");
            Append(ref builder, cause, depth + 1, seen, ref visits);
        }
    }
}

public sealed partial class SafeLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new SafeLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();

    private sealed class SafeLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            state is string ? inner.BeginScope(state) : inner.BeginScope(SafeState.From(state));

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }

            var scrubbed = exception is null ? null : new ScrubbedException(exception);
            if (state is string text)
            {
                inner.Log(logLevel, eventId, text, scrubbed, static (value, _) => value);
                return;
            }

            inner.Log(logLevel, eventId, SafeState.From(state), scrubbed, static (value, _) => value.ToString());
        }
    }

    private sealed class SafeState(IReadOnlyList<KeyValuePair<string, object?>> values, string message)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private const int MaxNodes = 2_000;
        private const int MaxElements = 100;
        private const int MaxDepth = 4;

        public int Count => values.Count;

        public KeyValuePair<string, object?> this[int index] => values[index];

        public static SafeState From<TState>(TState state)
        {
            List<KeyValuePair<string, object?>> pairs;
            try
            {
                pairs = state switch
                {
                    null => [],
                    Exception exception => [new("Exception", exception)],
                    IZLoggerEntryCreatable zlogger => ZLoggerPairs(zlogger),
                    IReadOnlyList<KeyValuePair<string, object?>> list => [.. list.Take(MaxElements)],
                    System.Collections.ICollection and IEnumerable<KeyValuePair<string, object?>> items => [.. items.Take(MaxElements)],
                    _ => [new("State", $"[{state.GetType().Name} log state withheld]")],
                };
            }
            catch (Exception inspection) when (inspection is not OutOfMemoryException)
            {
                pairs = [new("State", $"[{state!.GetType().Name} log state withheld]")];
            }

            var budget = new Budget();
            var cleaned = pairs
                .Select(pair => new KeyValuePair<string, object?>(pair.Key ?? string.Empty, Normalize(pair.Value, 0, budget)))
                .OrderBy(pair => pair.Key == "{OriginalFormat}")
                .ToList();
            var template = cleaned.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;
            var message = template is null
                ? string.Join(", ", cleaned.Select(pair => $"{pair.Key}={pair.Value}"))
                : Format(template, cleaned);
            return new SafeState(cleaned, message);
        }

        private static readonly LogInfo DecodingInfo = new(new LogCategory(string.Empty), default, LogLevel.None, default, null, null, default, null, string.Empty, string.Empty, 0);

        private static List<KeyValuePair<string, object?>> ZLoggerPairs(IZLoggerEntryCreatable zlogger)
        {
            var entry = zlogger.CreateEntry(in DecodingInfo);
            (zlogger as IReferenceCountable)?.Retain();
            try
            {
                var pairs = new List<KeyValuePair<string, object?>>(entry.ParameterCount + 1);
                for (var position = 0; position < entry.ParameterCount; position++)
                {
                    pairs.Add(new(entry.GetParameterKeyAsString(position), entry.GetParameterValue(position)));
                }

                pairs.Add(new("{OriginalFormat}", entry.GetOriginalFormat()));
                return pairs;
            }
            finally
            {
                entry.Return();
            }
        }

        private static string Format(string template, List<KeyValuePair<string, object?>> values)
        {
            var arguments = values.Where(pair => pair.Key != "{OriginalFormat}").Select(pair => pair.Value ?? "(null)").ToArray();
            using var builder = ZString.CreateStringBuilder();
            var index = 0;
            for (var position = 0; position < template.Length; position++)
            {
                var current = template[position];
                if (current is '{' or '}' && position + 1 < template.Length && template[position + 1] == current)
                {
                    builder.Append(current, 2);
                    position++;
                    continue;
                }

                if (current == '{' && template.IndexOf('}', position) is var end and > 0)
                {
                    var hole = template[(position + 1)..end];
                    var suffix = hole.IndexOfAny([',', ':']) is var split and >= 0 ? hole[split..] : string.Empty;
                    if (suffix.StartsWith(','))
                    {
                        var rest = suffix[1..];
                        var cut = rest.IndexOfAny([',', ':']);
                        var head = cut >= 0 ? rest[..cut] : rest;
                        if (!int.TryParse(head, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
                        {
                            suffix = ':' + rest;
                        }
                    }

                    builder.Append('{');
                    builder.Append(index++.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    builder.Append(suffix);
                    builder.Append('}');
                    position = end;
                    continue;
                }

                builder.Append(current is '{' or '}' ? new string(current, 2) : current.ToString());
            }

            try
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, builder.ToString(), arguments);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        public override string ToString() => message;

        private static bool IsSafeScalar(object value) =>
            value is string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or Enum or DateTime or DateTimeOffset or TimeSpan or Guid
                or PathString or HostString or EventId
            || value.GetType() == typeof(Version)
            || value.GetType() == typeof(System.Net.IPAddress)
            || value.GetType() == typeof(System.Net.IPEndPoint);

        private static object? Normalize(object? value, int depth, Budget budget) => value switch
        {
            null => null,
            _ when IsSafeScalar(value) => value,
            _ => Render(value, depth, budget),
        };

        private static string Render(object? value, int depth, Budget budget)
        {
            if (!budget.Take())
            {
                return "…";
            }

            return value switch
            {
                null => string.Empty,
                _ when IsSafeScalar(value) => value.ToString() ?? string.Empty,
                Exception exception => new ScrubbedException(exception).ToString(),
                Endpoint endpoint => endpoint.DisplayName ?? "(endpoint)",
                System.Collections.ICollection items when depth < MaxDepth => RenderItems(items, depth, budget),
                _ => $"[{value.GetType().Name} withheld]",
            };
        }

        private static string RenderItems(System.Collections.ICollection items, int depth, Budget budget)
        {
            var parts = new List<string>();
            try
            {
                foreach (var item in items)
                {
                    if (parts.Count >= MaxElements || budget.Spent)
                    {
                        parts.Add("…");
                        break;
                    }

                    parts.Add(string.Join("=", Parts(item).Select(part => Render(part, depth + 1, budget))));
                }
            }
            catch (Exception inspection) when (inspection is not OutOfMemoryException)
            {
                return $"[{items.GetType().Name} withheld]";
            }

            return "[" + string.Join(", ", parts) + "]";
        }

        private static IEnumerable<object?> Parts(object? item)
        {
            if (item is System.Collections.DictionaryEntry entry)
            {
                return [entry.Key, entry.Value];
            }

            if (item?.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                return [type.GetProperty("Key")!.GetValue(item), type.GetProperty("Value")!.GetValue(item)];
            }

            return [item];
        }

        private sealed class Budget
        {
            private int used;

            public bool Spent => this.used >= MaxNodes;

            public bool Take() => ++this.used <= MaxNodes;
        }
    }

}

public static class SafeLoggingExtensions
{
    public static IServiceCollection AddSafeLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(provider =>
            new SafeLoggerFactory(ActivatorUtilities.CreateInstance<LoggerFactory>(provider))));
        return services;
    }
}
