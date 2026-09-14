namespace TerraBase;

/// <summary>Severity values understood by the package-neutral logging boundary.</summary>
public enum ObservabilityLogLevel
{
    /// <summary>Diagnostic information useful while developing.</summary>
    Trace,

    /// <summary>Diagnostic information useful during normal operation.</summary>
    Debug,

    /// <summary>Normal service activity.</summary>
    Information,

    /// <summary>A recoverable condition that may require attention.</summary>
    Warning,

    /// <summary>A failed operation or degraded subsystem.</summary>
    Error,

    /// <summary>A process-level failure requiring immediate attention.</summary>
    Critical,
}

/// <summary>
/// One structured log event. The shape intentionally contains no ZLogger types,
/// allowing an adapter to use ZLogger when that package is selected by the host.
/// </summary>
public sealed record ObservabilityLogEvent
{
    /// <summary>Creates a structured log event.</summary>
    /// <param name="level">The event severity.</param>
    /// <param name="eventName">The stable event name.</param>
    /// <param name="messageTemplate">The message template.</param>
    /// <param name="properties">The structured event properties.</param>
    /// <param name="exception">The optional exception associated with the event.</param>
    public ObservabilityLogEvent(
        ObservabilityLogLevel level,
        string eventName,
        string messageTemplate,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTemplate);
        Level = level;
        EventName = eventName;
        MessageTemplate = messageTemplate;
        Properties = properties ?? EmptyProperties;
        Exception = exception;
    }

    private static IReadOnlyDictionary<string, object?> EmptyProperties { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>The event severity.</summary>
    public ObservabilityLogLevel Level { get; }

    /// <summary>The stable event name.</summary>
    public string EventName { get; }

    /// <summary>The structured message template.</summary>
    public string MessageTemplate { get; }

    /// <summary>The structured event properties.</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }

    /// <summary>The optional exception associated with the event.</summary>
    public Exception? Exception { get; }
}

/// <summary>
/// Adapter boundary for ZLogger. Implementations can forward the event to
/// source-generated ZLogger methods without making TerraBase depend on ZLogger.
/// </summary>
public interface IZLoggerAdapter
{
    /// <summary>Writes one structured event.</summary>
    /// <param name="logEvent">The event to write.</param>
    void Write(ObservabilityLogEvent logEvent);
}

/// <summary>
/// Adapter boundary for ZString. Implementations can format templates with a
/// pooled builder without making TerraBase depend on ZString.
/// </summary>
public interface IZStringFormatter
{
    /// <summary>Formats a template with structured properties.</summary>
    /// <param name="messageTemplate">The message template.</param>
    /// <param name="properties">The property values used by the template.</param>
    /// <returns>The formatted message.</returns>
    string Format(string messageTemplate, IReadOnlyDictionary<string, object?> properties);
}

/// <summary>Common logging boundary used by TerraBase components.</summary>
public interface IObservabilityLogger
{
    /// <summary>Writes one structured event.</summary>
    /// <param name="logEvent">The event to write.</param>
    void Log(ObservabilityLogEvent logEvent);
}

/// <summary>A logger that forwards events to a package-specific adapter.</summary>
public sealed class AdapterObservabilityLogger : IObservabilityLogger
{
    private readonly IZLoggerAdapter adapter;

    /// <summary>Creates an adapter-backed logger.</summary>
    /// <param name="adapter">The package-specific adapter.</param>
    public AdapterObservabilityLogger(IZLoggerAdapter adapter) =>
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    /// <inheritdoc />
    public void Log(ObservabilityLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        adapter.Write(logEvent);
    }
}

/// <summary>A logger implementation that intentionally discards events.</summary>
public sealed class NullObservabilityLogger : IObservabilityLogger
{
    /// <summary>The shared no-op logger.</summary>
    public static NullObservabilityLogger Instance { get; } = new();

    private NullObservabilityLogger()
    {
    }

    /// <inheritdoc />
    public void Log(ObservabilityLogEvent logEvent) => ArgumentNullException.ThrowIfNull(logEvent);
}

/// <summary>A string formatter that preserves the template for hosts without ZString.</summary>
public sealed class TemplateObservabilityFormatter : IZStringFormatter
{
    /// <inheritdoc />
    public string Format(string messageTemplate, IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTemplate);
        ArgumentNullException.ThrowIfNull(properties);

        var formatted = messageTemplate;
        foreach (var property in properties)
        {
            formatted = formatted.Replace(
                $"{{{property.Key}}}",
                property.Value?.ToString() ?? string.Empty,
                StringComparison.Ordinal);
        }

        return formatted;
    }
}
