using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Rows;

internal closed record class RowOperation(string Table);

internal sealed record class CreateRow(string Table, IReadOnlyDictionary<string, object?> Values) : RowOperation(Table);

internal sealed record class UpdateRow(string Table, long Key, IReadOnlyDictionary<string, object?> Values) : RowOperation(Table);

internal sealed record class DeleteRow(string Table, long Key) : RowOperation(Table);

internal sealed record RowBody(IReadOnlyDictionary<string, JsonElement>? Row);

internal sealed record RowsBatchBody(IReadOnlyList<RowsBatchOperation?>? Operations);

internal sealed record RowsBatchOperation(string? Op, string? Table, string? Key, IReadOnlyDictionary<string, JsonElement>? Row);

[JsonConverter(typeof(RowValuesConverter))]
internal sealed record RowValues(IReadOnlyList<KeyValuePair<string, object?>> Columns);

internal sealed record RowAnswer(string Key, RowValues? Row);

internal sealed record RowsBatchAnswer(IReadOnlyList<RowAnswer> Results);

internal sealed record RowsErrorBody(RowsError Error);

internal sealed record RowsError(string Code, string Message, RowsErrorDetails? Details);

internal sealed record RowsErrorDetails(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Operation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Table = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Column = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Clause = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null);

internal sealed record RowRefused(int Status, string Code, string Message, RowsErrorDetails? Details = null);

internal readonly union RowsOutcome(IReadOnlyList<RowAnswer>, RowRefused);

internal sealed class RowValuesConverter : JsonConverter<RowValues>
{
    public override RowValues Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("rows are response-only");

    public override void Write(Utf8JsonWriter writer, RowValues value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (name, cell) in value.Columns)
        {
            writer.WritePropertyName(name);
            switch (cell)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case long integer:
                    writer.WriteNumberValue(integer);
                    break;
                case double real:
                    writer.WriteNumberValue(real);
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case byte[] blob:
                    writer.WriteStartObject();
                    writer.WriteBase64String("base64", blob);
                    writer.WriteEndObject();
                    break;
                default:
                    throw new InvalidOperationException("a stored value is null, an integer, a real, text or a blob");
            }
        }

        writer.WriteEndObject();
    }
}
