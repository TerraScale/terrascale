using System.Collections.ObjectModel;
using System.Text.Json;

namespace TerraBase;

/// <summary>The kinds of values a TerraBase document can contain.</summary>
public enum DocumentValueKind
{
    /// <summary>A JSON null value.</summary>
    Null,

    /// <summary>A Boolean value.</summary>
    Boolean,

    /// <summary>A signed 64-bit integer.</summary>
    Integer,

    /// <summary>An IEEE-754 double precision number.</summary>
    Number,

    /// <summary>A UTF-8 string.</summary>
    String,

    /// <summary>An opaque byte sequence.</summary>
    Binary,

    /// <summary>An ordered array of values.</summary>
    Array,

    /// <summary>An object with string field names.</summary>
    Object,
}

/// <summary>
/// An immutable, recursively nested document value. The value model is
/// independent of JSON and can therefore be used by storage and transport
/// codecs without coupling either codec to a wire representation.
/// </summary>
public sealed class DocumentValue : IEquatable<DocumentValue>
{
    private static readonly DocumentValue NullValue = new(DocumentValueKind.Null, null);
    private readonly object? value;

    private DocumentValue(DocumentValueKind kind, object? value)
    {
        Kind = kind;
        this.value = value;
    }

    /// <summary>The shared null value.</summary>
    public static DocumentValue Null => NullValue;

    /// <summary>The value kind.</summary>
    public DocumentValueKind Kind { get; }

    /// <summary>Creates a Boolean value.</summary>
    public static DocumentValue Boolean(bool value) => new(DocumentValueKind.Boolean, value);

    /// <summary>Creates an integer value.</summary>
    public static DocumentValue Integer(long value) => new(DocumentValueKind.Integer, value);

    /// <summary>Creates a floating-point value.</summary>
    public static DocumentValue Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Document numbers must be finite.");
        }

        return new(DocumentValueKind.Number, value);
    }

    /// <summary>Creates a string value.</summary>
    public static DocumentValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(DocumentValueKind.String, value);
    }

    /// <summary>Creates a binary value from a defensive copy of the bytes.</summary>
    public static DocumentValue Binary(ReadOnlySpan<byte> value) =>
        new(DocumentValueKind.Binary, value.ToArray());

    /// <summary>Creates an array value from a defensive copy of the items.</summary>
    public static DocumentValue Array(IEnumerable<DocumentValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.ToArray();
        if (items.Any(static item => item is null))
        {
            throw new ArgumentException("Document arrays cannot contain null references.", nameof(values));
        }

        return new(DocumentValueKind.Array, Array.AsReadOnly(items));
    }

    /// <summary>Creates an object value from a defensive, ordinal-keyed copy.</summary>
    public static DocumentValue Object(IEnumerable<KeyValuePair<string, DocumentValue>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var copy = new SortedDictionary<string, DocumentValue>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ValidateFieldName(field.Key);
            ArgumentNullException.ThrowIfNull(field.Value, nameof(fields));
            if (!copy.TryAdd(field.Key, field.Value))
            {
                throw new ArgumentException($"The document field '{field.Key}' is duplicated.", nameof(fields));
            }
        }

        return new(DocumentValueKind.Object, new ReadOnlyDictionary<string, DocumentValue>(copy));
    }

    /// <summary>Creates an object value from a dictionary.</summary>
    public static DocumentValue Object(IReadOnlyDictionary<string, DocumentValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return Object(fields.AsEnumerable());
    }

    /// <summary>Gets the Boolean value.</summary>
    public bool BooleanValue => Get<bool>(DocumentValueKind.Boolean);

    /// <summary>Gets the integer value.</summary>
    public long IntegerValue => Get<long>(DocumentValueKind.Integer);

    /// <summary>Gets the floating-point value.</summary>
    public double NumberValue => Get<double>(DocumentValueKind.Number);

    /// <summary>Gets the string value.</summary>
    public string StringValue => Get<string>(DocumentValueKind.String);

    /// <summary>Gets a defensive copy of the binary value.</summary>
    public byte[] BinaryValue => ((byte[])Get<byte[]>(DocumentValueKind.Binary)).ToArray();

    /// <summary>Gets the immutable array items.</summary>
    public IReadOnlyList<DocumentValue> ArrayValue => Get<IReadOnlyList<DocumentValue>>(DocumentValueKind.Array);

    /// <summary>Gets the immutable object fields.</summary>
    public IReadOnlyDictionary<string, DocumentValue> ObjectValue =>
        Get<IReadOnlyDictionary<string, DocumentValue>>(DocumentValueKind.Object);

    /// <summary>Gets one object field, or <see langword="null"/> when absent.</summary>
    public DocumentValue? GetField(string name)
    {
        ValidateFieldName(name);
        return Kind == DocumentValueKind.Object && ObjectValue.TryGetValue(name, out var result) ? result : null;
    }

    /// <summary>Converts a JSON element into a typed nested value.</summary>
    public static DocumentValue FromJson(JsonElement element) => FromJson(element, 0);

    /// <summary>Serializes this value as a detached JSON element.</summary>
    public JsonElement ToJson()
    {
        using var document = JsonDocument.Parse(ToJsonText());
        return document.RootElement.Clone();
    }

    /// <inheritdoc />
    public bool Equals(DocumentValue? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            DocumentValueKind.Null => true,
            DocumentValueKind.Boolean => BooleanValue == other.BooleanValue,
            DocumentValueKind.Integer => IntegerValue == other.IntegerValue,
            DocumentValueKind.Number => NumberValue.Equals(other.NumberValue),
            DocumentValueKind.String => StringValue == other.StringValue,
            DocumentValueKind.Binary => BinaryValue.AsSpan().SequenceEqual(other.BinaryValue),
            DocumentValueKind.Array => ArrayValue.SequenceEqual(other.ArrayValue),
            DocumentValueKind.Object => ObjectValue.Count == other.ObjectValue.Count &&
                ObjectValue.All(pair => other.ObjectValue.TryGetValue(pair.Key, out var item) && pair.Value.Equals(item)),
            _ => false,
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DocumentValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((int)Kind);
        switch (Kind)
        {
            case DocumentValueKind.Boolean:
                hash.Add(BooleanValue);
                break;
            case DocumentValueKind.Integer:
                hash.Add(IntegerValue);
                break;
            case DocumentValueKind.Number:
                hash.Add(NumberValue);
                break;
            case DocumentValueKind.String:
                hash.Add(StringValue, StringComparer.Ordinal);
                break;
            case DocumentValueKind.Binary:
                foreach (var byteValue in (byte[])value!)
                {
                    hash.Add(byteValue);
                }

                break;
            case DocumentValueKind.Array:
                foreach (var item in ArrayValue)
                {
                    hash.Add(item);
                }

                break;
            case DocumentValueKind.Object:
                foreach (var field in ObjectValue)
                {
                    hash.Add(field.Key, StringComparer.Ordinal);
                    hash.Add(field.Value);
                }

                break;
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => ToJsonText();

    private static DocumentValue FromJson(JsonElement element, int depth)
    {
        if (depth > DocumentCodecLimits.Default.MaxDepth)
        {
            throw new ArgumentException("The JSON value exceeds the maximum document depth.", nameof(element));
        }

        return element.ValueKind switch
        {
            JsonValueKind.Null => Null,
            JsonValueKind.True => Boolean(true),
            JsonValueKind.False => Boolean(false),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => Integer(integer),
            JsonValueKind.Number => Number(element.GetDouble()),
            JsonValueKind.String => String(element.GetString()!),
            JsonValueKind.Array => Array(element.EnumerateArray().Select(item => FromJson(item, depth + 1))),
            JsonValueKind.Object => Object(element.EnumerateObject().Select(property =>
                new KeyValuePair<string, DocumentValue>(property.Name, FromJson(property.Value, depth + 1)))),
            _ => throw new ArgumentException("The JSON value is undefined.", nameof(element)),
        };
    }

    private string ToJsonText()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteJson(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteJson(Utf8JsonWriter writer)
    {
        switch (Kind)
        {
            case DocumentValueKind.Null:
                writer.WriteNullValue();
                break;
            case DocumentValueKind.Boolean:
                writer.WriteBooleanValue(BooleanValue);
                break;
            case DocumentValueKind.Integer:
                writer.WriteNumberValue(IntegerValue);
                break;
            case DocumentValueKind.Number:
                writer.WriteNumberValue(NumberValue);
                break;
            case DocumentValueKind.String:
                writer.WriteStringValue(StringValue);
                break;
            case DocumentValueKind.Binary:
                writer.WriteBase64StringValue((byte[])value!);
                break;
            case DocumentValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in ArrayValue)
                {
                    item.WriteJson(writer);
                }

                writer.WriteEndArray();
                break;
            case DocumentValueKind.Object:
                writer.WriteStartObject();
                foreach (var field in ObjectValue)
                {
                    writer.WritePropertyName(field.Key);
                    field.Value.WriteJson(writer);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException($"Unsupported document value kind {Kind}.");
        }
    }

    private T Get<T>(DocumentValueKind expected)
    {
        if (Kind != expected)
        {
            throw new InvalidOperationException($"Expected a {expected} document value, but found {Kind}.");
        }

        return (T)value!;
    }

    private static void ValidateFieldName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Any(char.IsControl) || Encoding.UTF8.GetByteCount(name) > 255)
        {
            throw new ArgumentException("Document field names must be non-empty, valid UTF-8 text of at most 255 bytes.", nameof(name));
        }
    }
}
