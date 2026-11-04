using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// A SQL argument: null, a 64-bit integer, a finite double, a boolean (stored as 1 or 0), text or a blob. Values other
/// than blobs convert implicitly, so <c>[42, "Ada", true, null]</c> is an argument list; blobs use <see cref="FromBytes(byte[])"/>.
/// </summary>
[JsonConverter(typeof(TerraBaseSqlValueConverter))]
public readonly record struct TerraBaseSqlValue
{
    private TerraBaseSqlValue(object? value) => Value = value;

    /// <summary>SQL NULL.</summary>
    public static TerraBaseSqlValue Null => default;

    /// <summary>The <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="string"/> or <see cref="byte"/> array sent, or null.</summary>
    public object? Value { get; }

    /// <summary>An integer argument.</summary>
    public static implicit operator TerraBaseSqlValue(long value) => new(value);

    /// <summary>An integer argument.</summary>
    public static implicit operator TerraBaseSqlValue(int value) => new((long)value);

    /// <summary>A finite real argument; NaN and infinities are refused.</summary>
    public static implicit operator TerraBaseSqlValue(double value) =>
        double.IsFinite(value) ? new(value) : throw new ArgumentOutOfRangeException(nameof(value), "SQL arguments must be finite numbers.");

    /// <summary>A boolean argument, stored as 1 or 0.</summary>
    public static implicit operator TerraBaseSqlValue(bool value) => new(value);

    /// <summary>A text argument; null is SQL NULL.</summary>
    public static implicit operator TerraBaseSqlValue(string? value) => new(value);

    /// <summary>An integer argument.</summary>
    public static TerraBaseSqlValue FromInt64(long value) => value;

    /// <summary>A finite real argument.</summary>
    public static TerraBaseSqlValue FromDouble(double value) => value;

    /// <summary>A boolean argument.</summary>
    public static TerraBaseSqlValue FromBoolean(bool value) => value;

    /// <summary>A text argument.</summary>
    public static TerraBaseSqlValue FromString(string? value) => value;

    /// <summary>A blob argument, sent as <c>{"base64":"…"}</c>; null is SQL NULL. The bytes are copied.</summary>
    public static TerraBaseSqlValue FromBytes(ReadOnlySpan<byte> value) => new(value.ToArray());

    /// <summary>A blob argument; null is SQL NULL. The bytes are copied.</summary>
    public static TerraBaseSqlValue FromBytes(byte[]? value) => value is null ? Null : FromBytes(value.AsSpan());
}

internal sealed class TerraBaseSqlValueConverter : JsonConverter<TerraBaseSqlValue>
{
    public override TerraBaseSqlValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("SQL arguments are request-only.");

    public override void Write(Utf8JsonWriter writer, TerraBaseSqlValue value, JsonSerializerOptions options)
    {
        switch (value.Value)
        {
            case long integer: writer.WriteNumberValue(integer); break;
            case double real: writer.WriteNumberValue(real); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case string text: writer.WriteStringValue(text); break;
            case byte[] blob:
                writer.WriteStartObject();
                writer.WriteBase64String("base64", blob);
                writer.WriteEndObject();
                break;
            default: writer.WriteNullValue(); break;
        }
    }
}
