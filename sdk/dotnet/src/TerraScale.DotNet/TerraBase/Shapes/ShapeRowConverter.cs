using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

internal sealed class ShapeRowConverter : JsonConverter<TerraBaseSqlRecord>
{
    public override TerraBaseSqlRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Malformed();
        var columns = new List<TerraBaseSqlColumn>();
        var cells = new List<TerraBaseSqlCell>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw Malformed();
            var name = reader.GetString() ?? throw Malformed();
            if (!ordinals.TryAdd(name, columns.Count) || !reader.Read()) throw Malformed();
            columns.Add(new(name));
            cells.Add(TerraBaseSqlRowsConverter.Cell(ref reader));
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw Malformed();
        return new(Array.AsReadOnly(columns.ToArray()), ordinals.AsReadOnly(), [.. cells], 0);
    }

    public override void Write(Utf8JsonWriter writer, TerraBaseSqlRecord value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Shape rows are response-only.");

    private static JsonException Malformed() => new("A shape row must be an object with distinct SQL column names and SQL cell values.");
}
