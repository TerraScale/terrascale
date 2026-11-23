using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

[JsonConverter(typeof(TerraBaseSqlValueConverter))]
public readonly record struct TerraBaseSqlValue
{
    private TerraBaseSqlValue(object? value) => Value = value;

    public static TerraBaseSqlValue Null => default;

    public object? Value { get; }

    public static implicit operator TerraBaseSqlValue(long value) => new(value);

    public static implicit operator TerraBaseSqlValue(int value) => new((long)value);

    public static implicit operator TerraBaseSqlValue(double value) =>
        double.IsFinite(value) ? new(value) : throw new ArgumentOutOfRangeException(nameof(value), "SQL arguments must be finite numbers.");

    public static implicit operator TerraBaseSqlValue(bool value) => new(value);

    public static implicit operator TerraBaseSqlValue(string? value) => new(value);

    public static TerraBaseSqlValue FromInt64(long value) => value;

    public static TerraBaseSqlValue FromDouble(double value) => value;

    public static TerraBaseSqlValue FromBoolean(bool value) => value;

    public static TerraBaseSqlValue FromString(string? value) => value;

    public static TerraBaseSqlValue FromBytes(ReadOnlySpan<byte> value) => new(value.ToArray());

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
