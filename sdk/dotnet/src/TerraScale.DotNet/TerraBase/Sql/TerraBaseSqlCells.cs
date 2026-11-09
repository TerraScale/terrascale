using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

internal readonly struct TerraBaseSqlCell
{
    private static readonly object NullValue = new();
    private readonly long integer;
    private readonly object? value;

    private TerraBaseSqlCell(long integer, object? value)
    {
        this.integer = integer;
        this.value = value;
    }

    public static TerraBaseSqlCell Null => new(0, NullValue);

    public JsonValueKind Kind => value switch
    {
        null or Real => JsonValueKind.Number,
        string => JsonValueKind.String,
        byte[] => JsonValueKind.Object,
        _ => JsonValueKind.Null,
    };

    public bool IsInteger => value is null;

    public long Integer => integer;

    public string? Text => value as string;

    public byte[]? Blob => value as byte[];

    public string? RealText => (value as Real)?.Text;

    public static TerraBaseSqlCell FromInteger(long integer) => new(integer, null);

    public static TerraBaseSqlCell FromReal(string text) => new(0, new Real(text));

    public static TerraBaseSqlCell FromText(string? text) => new(0, text ?? throw new JsonException("A SQL text value cannot be null."));

    public static TerraBaseSqlCell FromBlob(byte[] blob) => new(0, blob);

    public object Box() => value switch
    {
        null => integer,
        Real real => double.Parse(real.Text, NumberStyles.Float, CultureInfo.InvariantCulture),
        string text => text,
        byte[] blob => blob.Clone(),
        _ => DBNull.Value,
    };

    public void WriteTo(Utf8JsonWriter writer, bool blobAsObject)
    {
        switch (value)
        {
            case null: writer.WriteNumberValue(integer); break;
            case Real real: writer.WriteRawValue(real.Text, skipInputValidation: true); break;
            case string text: writer.WriteStringValue(text); break;
            case byte[] blob when blobAsObject:
                writer.WriteStartObject();
                writer.WriteBase64String("base64"u8, blob);
                writer.WriteEndObject();
                break;
            case byte[] blob: writer.WriteBase64StringValue(blob); break;
            default: writer.WriteNullValue(); break;
        }
    }

    private sealed record Real(string Text);
}

[JsonConverter(typeof(TerraBaseSqlRowsConverter))]
internal sealed class TerraBaseSqlRows(TerraBaseSqlCell[] cells, int count, bool rectangular, int width)
{
    public TerraBaseSqlCell[] Cells => cells;

    public int Count => count;

    public bool Fits(int columns) => count == 0 || (rectangular && width == columns);
}

internal sealed class TerraBaseSqlRowsConverter : JsonConverter<TerraBaseSqlRows>
{
    public override TerraBaseSqlRows Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Expect(reader.TokenType == JsonTokenType.StartArray);
        var buffer = ArrayPool<TerraBaseSqlCell>.Shared.Rent(64);
        var length = 0;
        var rows = 0;
        var width = -1;
        var rectangular = true;
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                Expect(reader.TokenType == JsonTokenType.StartArray);
                var count = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (length == buffer.Length)
                    {
                        var grown = ArrayPool<TerraBaseSqlCell>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, length).CopyTo(grown);
                        ArrayPool<TerraBaseSqlCell>.Shared.Return(buffer, clearArray: true);
                        buffer = grown;
                    }

                    buffer[length++] = Cell(ref reader);
                    count++;
                }

                rectangular &= width < 0 || width == count;
                width = count;
                rows++;
            }

            return new(buffer.AsSpan(0, length).ToArray(), rows, rectangular, width);
        }
        finally
        {
            ArrayPool<TerraBaseSqlCell>.Shared.Return(buffer, clearArray: true);
        }
    }

    public override void Write(Utf8JsonWriter writer, TerraBaseSqlRows value, JsonSerializerOptions options) =>
        throw new NotSupportedException("SQL rows are response-only.");

    private static TerraBaseSqlCell Cell(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return TerraBaseSqlCell.Null;
            case JsonTokenType.String:
                return TerraBaseSqlCell.FromText(reader.GetString());
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var integer)
                    ? TerraBaseSqlCell.FromInteger(integer)
                    : TerraBaseSqlCell.FromReal(Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan));
            case JsonTokenType.StartObject:
                Expect(reader.Read() && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("base64"u8));
                Expect(reader.Read() && reader.TokenType == JsonTokenType.String);
                if (!reader.TryGetBytesFromBase64(out var blob)) throw Malformed();
                Expect(reader.Read() && reader.TokenType == JsonTokenType.EndObject);
                return TerraBaseSqlCell.FromBlob(blob);
            default:
                throw Malformed();
        }
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw Malformed();
    }

    private static JsonException Malformed() => new("Each SQL row holds one null, number, string or blob per column.");
}
