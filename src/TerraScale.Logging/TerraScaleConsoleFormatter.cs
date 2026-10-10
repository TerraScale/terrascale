using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace TerraScale.Logging;

/// <summary>Writes each log entry as one line of compact JSON (TerraScale record v1).</summary>
/// <remarks>
/// Keys are written in a fixed order. State and scope properties that reuse a fixed key move to <c>attr.&lt;key&gt;</c>;
/// any other repeated key keeps its first value. Formatting never throws: a failing <c>ToString()</c> becomes <c>&lt;unformattable&gt;</c>.
/// </remarks>
public sealed class TerraScaleConsoleFormatter : ConsoleFormatter
{
    /// <summary>Name the formatter is registered under.</summary>
    public const string FormatterName = "terrascale";

    private const string OriginalFormat = "{OriginalFormat}";
    private const string Unformattable = "<unformattable>";
    private const string AttributePrefix = "attr.";
    private const int TimestampLength = 27;

    // Keys this formatter writes itself; a state or scope property with one of these names becomes attr.<name>.
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
    {
        "time", "level", "msg", "service", "region", "node", "trace_id", "span_id", "component", "event_id", "error", "stack",
    };

    // One buffer per thread, reused across records. Taken while in use so a re-entrant log call gets its own.
    [ThreadStatic]
    private static Record? _spare;

    private readonly string _service;
    private readonly string _region;
    private readonly string _node;
    private readonly bool _journal;

    /// <summary>Reads the TS_* and JOURNAL_STREAM variables once; <paramref name="environment"/> is replaced in tests.</summary>
    public TerraScaleConsoleFormatter(IOptions<TerraScaleFormatterOptions> options, Func<string, string?>? environment = null)
        : base(FormatterName)
    {
        environment ??= Environment.GetEnvironmentVariable;
        _service = environment("TS_SERVICE") is { Length: > 0 } service ? service : options.Value.Service;
        _region = environment("TS_REGION") ?? string.Empty;
        _node = environment("TS_NODE") ?? string.Empty;
        _journal = !string.IsNullOrEmpty(environment("JOURNAL_STREAM"));
    }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var record = _spare ?? new Record();
        _spare = null;
        try
        {
            record.Reset();
            var message = MessageOf(in logEntry);
            try
            {
                WriteRecord(record, in logEntry, scopeProvider, message);
            }
            catch (Exception)
            {
                record.Reset();
                record.Json.WriteStartObject();
                WriteHeader(record.Json, logEntry.LogLevel, Unformattable);
                record.Json.WriteEndObject();
            }

            record.Json.Flush();
            if (_journal)
            {
                textWriter.Write(JournalPriority(logEntry.LogLevel));
            }

            textWriter.Write(Encoding.UTF8.GetString(record.Bytes.WrittenSpan));
            textWriter.WriteLine();
        }
        finally
        {
            _spare = record;
        }
    }

    private void WriteRecord<TState>(Record record, in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, string message)
    {
        var json = record.Json;
        json.WriteStartObject();
        WriteHeader(json, logEntry.LogLevel, message);
        WriteIfPresent(json, "service", _service);
        WriteIfPresent(json, "region", _region);
        WriteIfPresent(json, "node", _node);

        var activity = Activity.Current;
        if (activity is not null && activity.TraceId != default)
        {
            json.WriteString("trace_id", activity.TraceId.ToHexString());
        }

        if (activity is not null && activity.SpanId != default)
        {
            json.WriteString("span_id", activity.SpanId.ToHexString());
        }

        json.WriteString("component", logEntry.Category);
        if (logEntry.EventId.Id != 0)
        {
            json.WriteNumber("event_id", logEntry.EventId.Id);
        }

        if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> properties)
        {
            foreach (var property in properties)
            {
                WriteProperty(record, property.Key, property.Value);
            }
        }

        scopeProvider?.ForEachScope(static (scope, state) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (var property in properties)
                {
                    WriteProperty(state, property.Key, property.Value);
                }
            }
        }, record);

        if (logEntry.Exception is { } exception)
        {
            json.WriteString("error", ExceptionSummary(exception));
            json.WriteString("stack", SafeToString(exception));
        }

        json.WriteEndObject();
    }

    private static void WriteHeader(Utf8JsonWriter json, LogLevel level, string message)
    {
        Span<char> stamp = stackalloc char[TimestampLength];
        DateTimeOffset.UtcNow.TryFormat(stamp, out var length, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        json.WriteString("time", stamp[..length]);
        json.WriteString("level", LevelName(level));
        json.WriteString("msg", message);
    }

    private static void WriteIfPresent(Utf8JsonWriter json, string key, string value)
    {
        if (value.Length > 0)
        {
            json.WriteString(key, value);
        }
    }

    private static void WriteProperty(Record record, string key, object? value)
    {
        if (key == OriginalFormat || record.ClaimKey(key) is not { } name)
        {
            return;
        }

        record.Json.WritePropertyName(name);
        WriteValue(record.Json, value);
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case int number:
                json.WriteNumberValue(number);
                break;
            case long number:
                json.WriteNumberValue(number);
                break;
            case decimal number:
                json.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                json.WriteNumberValue(number);
                break;
            default:
                json.WriteStringValue(SafeToString(value));
                break;
        }
    }

    private static string MessageOf<TState>(in LogEntry<TState> logEntry)
    {
        if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> properties)
        {
            foreach (var property in properties)
            {
                if (property.Key == OriginalFormat && property.Value is string template)
                {
                    return template;
                }
            }
        }

        try
        {
            return logEntry.Formatter(logEntry.State, logEntry.Exception);
        }
        catch (Exception)
        {
            return Unformattable;
        }
    }

    private static string ExceptionSummary(Exception exception)
    {
        try
        {
            return $"{exception.GetType().FullName}: {exception.Message}";
        }
        catch (Exception)
        {
            return Unformattable;
        }
    }

    private static string SafeToString(object value)
    {
        try
        {
            return value.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return Unformattable;
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Warning => "WARN",
        LogLevel.Error or LogLevel.Critical => "ERROR",
        _ => "INFO",
    };

    // Syslog priorities; journald strips the prefix and stores PRIORITY.
    // INFO records are state changes (notice); often-run units use LogLevelMax=notice to drop systemd's per-run lines.
    private static string JournalPriority(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "<7>",
        LogLevel.Warning => "<4>",
        LogLevel.Error or LogLevel.Critical => "<3>",
        _ => "<5>",
    };

    private sealed class Record
    {
        private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

        public Record()
        {
            Json = new Utf8JsonWriter(Bytes, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        public ArrayBufferWriter<byte> Bytes { get; } = new();

        public Utf8JsonWriter Json { get; }

        public void Reset()
        {
            Bytes.Clear();
            Json.Reset(Bytes);
            _claimed.Clear();
        }

        /// <summary>Returns the key to write, or null when an earlier property already used it.</summary>
        public string? ClaimKey(string key)
        {
            var name = ReservedKeys.Contains(key) ? AttributePrefix + key : key;
            return _claimed.Add(name) ? name : null;
        }
    }
}
