using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Rows;

public sealed record RowInput(string? Project, string? Database, string? Table, string? Key, IReadOnlyDictionary<string, JsonElement>? Row);

public sealed record RowsBatchOperation(string? Op, string? Table, string? Key, IReadOnlyDictionary<string, JsonElement>? Row);

public sealed record RowsBatchInput(string? Project, string? Database, IReadOnlyList<RowsBatchOperation?>? Operations);

public sealed record RowAnswer(string Key, [property: JsonConverter(typeof(RowValuesConverter))] IReadOnlyList<KeyValuePair<string, SqlValue>>? Row);

public sealed record RowsBatchAnswer(IReadOnlyList<RowAnswer> Results);

public sealed class RowValuesConverter : JsonConverter<IReadOnlyList<KeyValuePair<string, SqlValue>>>
{
    public override IReadOnlyList<KeyValuePair<string, SqlValue>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("rows are response-only");

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<KeyValuePair<string, SqlValue>> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (column, cell) in value)
        {
            writer.WritePropertyName(column);
            SqlRowsConverter.WriteValue(writer, cell);
        }

        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RowInput))]
[JsonSerializable(typeof(RowsBatchInput))]
[JsonSerializable(typeof(RowAnswer))]
[JsonSerializable(typeof(RowsBatchAnswer))]
[JsonSerializable(typeof(RowsErrorEnvelope))]
internal sealed partial class RowsJsonContext : JsonSerializerContext;
