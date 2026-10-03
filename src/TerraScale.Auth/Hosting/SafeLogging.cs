using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Auth.Hosting;

/// <summary>
/// A log-safe stand-in for an exception: the original type names and stack traces, never their
/// messages. Messages are free text that can carry credentials (URLs with codes, connection
/// strings, upstream error bodies), so no log sink receives them. The cause chain is kept as
/// <see cref="Exception.InnerException"/> (all children of an <see cref="AggregateException"/>
/// appear in the rendering) and <see cref="StackTrace"/> reports the original frames.
/// </summary>
public sealed class ScrubbedException : Exception
{
    private readonly string? originalStackTrace;
    private readonly string rendered;

    private const int MaxDepth = 16;

    /// <summary>Wraps <paramref name="original"/> and, recursively, its causes.</summary>
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

    /// <summary>Required by CA1032; use the wrapping constructor.</summary>
    public ScrubbedException()
        : this(new InvalidOperationException())
    {
    }

    /// <summary>Required by CA1032; the message is discarded like any other.</summary>
    public ScrubbedException(string message)
        : this(new InvalidOperationException(message))
    {
    }

    /// <summary>Required by CA1032; wraps <paramref name="innerException"/> and discards <paramref name="message"/>.</summary>
    public ScrubbedException(string message, Exception innerException)
        : this(new InvalidOperationException(message, innerException))
    {
    }

    /// <summary>Full name of the wrapped exception type.</summary>
    public string OriginalType { get; } = string.Empty;

    /// <inheritdoc />
    public override string? StackTrace => this.originalStackTrace;

    /// <inheritdoc />
    public override string ToString() => this.rendered;

    // The wrapped chain stops at the same depth the rendering does.
    // Frames come from the runtime's own trace data, never the virtual StackTrace property,
    // which a custom exception could override to return its message (or to throw).
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

    // Rendering is bounded in depth and in total exceptions, and each exception object is
    // rendered once, so a self-referencing aggregate graph cannot blow up a log call.
    private const int MaxRendered = 64;

    private static string Render(Exception root, int depth)
    {
        var builder = new StringBuilder();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var visits = 0;
        Append(builder, root, depth, seen, ref visits);
        return builder.ToString().TrimEnd();
    }

    // Every visit counts against the budget, including repeated and depth-limited ones, so
    // neither a wide aggregate of one shared child nor a deep fan-out can grow the output.
    private static void Append(StringBuilder builder, Exception exception, int depth, HashSet<Exception> seen, ref int visits)
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

        // An aggregate's InnerException is only its first child; render every one.
        IEnumerable<Exception> causes = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } inner ? [inner] : [];
        foreach (var cause in causes)
        {
            // Stop walking a wide aggregate as soon as the shared budget is spent.
            if (visits >= MaxRendered)
            {
                builder.AppendLine(" ---> (further causes truncated)");
                break;
            }

            builder.Append(" ---> ");
            Append(builder, cause, depth + 1, seen, ref visits);
        }
    }
}

/// <summary>
/// Wraps the logger factory so every provider (console, OpenTelemetry, Sentry, anything added
/// later) receives exceptions only as <see cref="ScrubbedException"/>, whether they arrive as the
/// exception argument, as a structured log value or as a scope. This covers framework and
/// library log calls as well as ts-auth's own.
/// </summary>
public sealed partial class SafeLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new SafeLogger(inner.CreateLogger(categoryName));

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();

    private sealed class SafeLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            state is string ? inner.BeginScope(state) : inner.BeginScope(SafeState.From(state));

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Entries no provider wants are dropped before any inspection work.
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }

            var scrubbed = exception is null ? null : new ScrubbedException(exception);
            if (state is string text)
            {
                // A string is its own message; it cannot render anything else.
                inner.Log(logLevel, eventId, text, scrubbed, static (value, _) => value);
                return;
            }

            // Every other state is rebuilt from its template and normalized values. Neither
            // the state's own ToString nor the caller's formatter ever runs, so no custom type
            // can render an exception message through them.
            inner.Log(logLevel, eventId, SafeState.From(state), scrubbed, static (value, _) => value.ToString());
        }
    }

    /// <summary>
    /// Structured log state rebuilt from a template and normalized values: scalars of exact,
    /// known types as they are, exceptions as their scrubbed rendering, collections as bounded
    /// renderings of their normalized elements, and every other object as a placeholder.
    /// </summary>
    private sealed class SafeState(IReadOnlyList<KeyValuePair<string, object?>> values, string message)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        // One budget per state across all of its values, nested collections and exception
        // graphs, so no small self-referencing input can expand into a huge rendering.
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
                // Only in-memory key/value shapes are read; lazy sequences are never enumerated.
                pairs = state switch
                {
                    null => [],
                    Exception exception => [new("Exception", exception)],
                    IReadOnlyList<KeyValuePair<string, object?>> list => [.. list.Take(MaxElements)],
                    System.Collections.ICollection and IEnumerable<KeyValuePair<string, object?>> items => [.. items.Take(MaxElements)],
                    _ => [new("State", $"[{state.GetType().Name} log state withheld]")],
                };
            }
            catch (Exception inspection) when (inspection is not OutOfMemoryException)
            {
                // A state that cannot be enumerated must never fail the operation being logged.
                pairs = [new("State", $"[{state!.GetType().Name} log state withheld]")];
            }

            var budget = new Budget();
            var cleaned = pairs
                .Select(pair => new KeyValuePair<string, object?>(pair.Key ?? string.Empty, Normalize(pair.Value, 0, budget)))
                .ToList();
            var template = cleaned.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;
            var message = template is null
                ? string.Join(", ", cleaned.Select(pair => $"{pair.Key}={pair.Value}"))
                : Format(template, cleaned);
            return new SafeState(cleaned, message);
        }

        // Message-template rendering with the standard semantics ({{ and }} escapes, alignment,
        // format specifiers, "(null)") over the normalized values, positionally like
        // Microsoft.Extensions.Logging. The original formatter is never called.
        private static string Format(string template, List<KeyValuePair<string, object?>> values)
        {
            var arguments = values.Where(pair => pair.Key != "{OriginalFormat}").Select(pair => pair.Value ?? "(null)").ToArray();
            var builder = new StringBuilder();
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
                    builder.Append('{').Append(index++.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(suffix).Append('}');
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

        // Scalars of exact framework types whose rendering is fixed. Reference types are
        // matched by exact runtime type, because a subclass could override ToString.
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
                // An endpoint is shown by its display name, read through the non-virtual property.
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
                // A collection that throws while enumerating is withheld, never rethrown.
                return $"[{items.GetType().Name} withheld]";
            }

            return "[" + string.Join(", ", parts) + "]";
        }

        // A dictionary entry contributes its key and its value; anything else is itself.
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

/// <summary>Registration for <see cref="SafeLoggerFactory"/>.</summary>
public static class SafeLoggingExtensions
{
    /// <summary>Replaces the logger factory with one that withholds exception messages from every provider.</summary>
    public static IServiceCollection AddSafeLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(provider =>
            new SafeLoggerFactory(ActivatorUtilities.CreateInstance<LoggerFactory>(provider))));
        return services;
    }
}
