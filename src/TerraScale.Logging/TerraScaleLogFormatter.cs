using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace TerraScale.Logging;

/// <summary>Writes each log entry as one line of compact JSON (TerraScale record v1).</summary>
/// <remarks>
/// Keys are written in a fixed order. State and scope properties that reuse a fixed key move to <c>attr.&lt;key&gt;</c>;
/// any other repeated key keeps its first value. Formatting never throws: a failing <c>ToString()</c> becomes <c>&lt;unformattable&gt;</c>.
/// </remarks>
public sealed class TerraScaleLogFormatter : IZLoggerFormatter
{
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
    public TerraScaleLogFormatter(string service, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        _service = environment("TS_SERVICE") is { Length: > 0 } name ? name : service;
        _region = environment("TS_REGION") ?? string.Empty;
        _node = environment("TS_NODE") ?? string.Empty;
        _journal = !string.IsNullOrEmpty(environment("JOURNAL_STREAM"));
    }

    /// <inheritdoc />
    public bool WithLineBreak => true;

    /// <inheritdoc />
    public void FormatLogEntry(IBufferWriter<byte> writer, IZLoggerEntry entry)
    {
        var record = _spare ?? new Record();
        _spare = null;
        try
        {
            var level = entry.LogInfo.LogLevel;
            var prefix = _journal ? JournalPriority(level) : ReadOnlySpan<byte>.Empty;
            record.Reset(prefix);
            try
            {
                WriteRecord(record, entry);
            }
            catch (Exception)
            {
                record.Reset(prefix);
                record.Json.WriteStartObject();
                WriteHeader(record.Json, level, Unformattable, entry.LogInfo.Timestamp.Utc);
                record.Json.WriteEndObject();
            }

            record.Finish();
            var bytes = record.Bytes.WrittenSpan;
            bytes.CopyTo(writer.GetSpan(bytes.Length));
            writer.Advance(bytes.Length);
        }
        finally
        {
            _spare = record;
        }
    }

    private void WriteRecord(Record record, IZLoggerEntry entry)
    {
        var json = record.Json;
        var info = entry.LogInfo;
        json.WriteStartObject();
        WriteHeader(json, info.LogLevel, MessageOf(entry), info.Timestamp.Utc);
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

        json.WriteString("component", info.Category.Name);
        if (info.EventId.Id != 0)
        {
            json.WriteNumber("event_id", info.EventId.Id);
        }

        for (var index = 0; index < entry.ParameterCount; index++)
        {
            var key = entry.GetParameterKeyAsString(index);
            if (key != OriginalFormat && record.ClaimKey(key) is { } name)
            {
                json.WritePropertyName(name);
                WriteParameter(json, entry, index);
            }
        }

        if (info.ScopeState is { } scope)
        {
            foreach (var property in scope.Properties)
            {
                WriteProperty(record, property.Key, property.Value);
            }
        }

        if (info.Exception is { } exception)
        {
            json.WriteString("error", ExceptionSummary(exception));
            json.WriteString("stack", SafeToString(exception));
        }

        json.WriteEndObject();
    }

    private static void WriteHeader(Utf8JsonWriter json, LogLevel level, string message, DateTimeOffset utc)
    {
        Span<char> stamp = stackalloc char[TimestampLength];
        utc.TryFormat(stamp, out var length, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
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
        if (key != OriginalFormat && record.ClaimKey(key) is { } name)
        {
            record.Json.WritePropertyName(name);
            WriteValue(record.Json, value);
        }
    }

    // Typed reads for the common types avoid boxing the value; every other type takes the boxed path.
    private static void WriteParameter(Utf8JsonWriter json, IZLoggerEntry entry, int index)
    {
        var type = entry.GetParameterType(index);
        if (type == typeof(string))
        {
            json.WriteStringValue(entry.GetParameterValue<string?>(index));
        }
        else if (type == typeof(bool))
        {
            json.WriteBooleanValue(entry.GetParameterValue<bool>(index));
        }
        else if (type == typeof(int))
        {
            json.WriteNumberValue(entry.GetParameterValue<int>(index));
        }
        else if (type == typeof(long))
        {
            json.WriteNumberValue(entry.GetParameterValue<long>(index));
        }
        else if (type == typeof(decimal))
        {
            json.WriteNumberValue(entry.GetParameterValue<decimal>(index));
        }
        else if (type == typeof(double))
        {
            var number = entry.GetParameterValue<double>(index);
            if (double.IsFinite(number))
            {
                json.WriteNumberValue(number);
            }
            else
            {
                json.WriteStringValue(SafeToString(number));
            }
        }
        else
        {
            WriteValue(json, entry.GetParameterValue(index));
        }
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

    private static string MessageOf(IZLoggerEntry entry)
    {
        try
        {
            var template = entry.GetOriginalFormat();
            return string.IsNullOrEmpty(template) ? entry.ToString() : template;
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
            return ZString.Concat(exception.GetType().FullName, ": ", exception.Message);
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
    private static ReadOnlySpan<byte> JournalPriority(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "<7>"u8,
        LogLevel.Warning => "<4>"u8,
        LogLevel.Error or LogLevel.Critical => "<3>"u8,
        _ => "<5>"u8,
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

        /// <summary>Starts an empty record, after the optional journald prefix.</summary>
        public void Reset(ReadOnlySpan<byte> prefix)
        {
            Bytes.Clear();
            Json.Reset(Bytes);
            _claimed.Clear();
            prefix.CopyTo(Bytes.GetSpan(prefix.Length));
            Bytes.Advance(prefix.Length);
        }

        /// <summary>Flushes the JSON and ends the line.</summary>
        public void Finish()
        {
            Json.Flush();
            Bytes.GetSpan(1)[0] = (byte)'\n';
            Bytes.Advance(1);
        }

        /// <summary>Returns the key to write, or null when an earlier property already used it.</summary>
        public string? ClaimKey(string key)
        {
            var name = ReservedKeys.Contains(key) ? AttributePrefix + key : key;
            return _claimed.Add(name) ? name : null;
        }
    }
}
