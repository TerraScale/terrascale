using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Rows;

public sealed record RowInput(string? Project, string? Database, string? Table, string? Key, IReadOnlyDictionary<string, JsonElement>? Row);

public sealed record RowsBatchOperation(string? Op, string? Table, string? Key, IReadOnlyDictionary<string, JsonElement>? Row);

public sealed record RowsBatchInput(string? Project, string? Database, IReadOnlyList<RowsBatchOperation?>? Operations);

public sealed record RowAnswer
{
    private readonly IReadOnlyList<KeyValuePair<string, SqlValue>>? _row;

    [JsonConstructor]
    public RowAnswer(string key, IReadOnlyList<KeyValuePair<string, SqlValue>>? row)
    {
        Key = key;
        _row = row;
    }

    internal RowAnswer(string key, IReadOnlyList<SqlColumn> columns, IReadOnlyList<SqlValue> values)
    {
        Key = key;
        Columns = columns;
        Values = values;
    }

    public string Key { get; init; }

    [JsonConverter(typeof(RowValuesConverter))]
    public IReadOnlyList<KeyValuePair<string, SqlValue>>? Row =>
        _row ?? (Columns is { } columns && Values is { } values
            ? (columns.Count <= 1 || values.Count <= 1 ? [] : new RowValuesView(columns, values))
            : null);

    internal IReadOnlyList<SqlColumn>? Columns { get; }

    internal IReadOnlyList<SqlValue>? Values { get; }

    public void Deconstruct(out string key, out IReadOnlyList<KeyValuePair<string, SqlValue>>? row)
    {
        key = Key;
        row = Row;
    }
}

public sealed record RowsBatchAnswer(IReadOnlyList<RowAnswer> Results);

internal sealed class RowValuesView(IReadOnlyList<SqlColumn> columns, IReadOnlyList<SqlValue> values) : IReadOnlyList<KeyValuePair<string, SqlValue>>
{
    public IReadOnlyList<SqlColumn> Columns => columns;

    public IReadOnlyList<SqlValue> Values => values;

    public int Count => Math.Max(0, Math.Min(columns.Count, values.Count) - 1);

    public KeyValuePair<string, SqlValue> this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return new KeyValuePair<string, SqlValue>(columns[index + 1].Name, values[index + 1]);
        }
    }

    public IEnumerator<KeyValuePair<string, SqlValue>> GetEnumerator()
    {
        var count = Count;
        for (var i = 0; i < count; i++)
        {
            yield return new KeyValuePair<string, SqlValue>(columns[i + 1].Name, values[i + 1]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class RowValuesConverter : JsonConverter<IReadOnlyList<KeyValuePair<string, SqlValue>>>
{
    public override IReadOnlyList<KeyValuePair<string, SqlValue>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("rows are response-only");

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<KeyValuePair<string, SqlValue>> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        if (value is RowValuesView view)
        {
            var columns = view.Columns;
            var values = view.Values;
            var count = Math.Min(columns.Count, values.Count);
            for (var i = 1; i < count; i++)
            {
                writer.WritePropertyName(columns[i].Name);
                SqlRowsConverter.WriteValue(writer, values[i]);
            }
        }
        else
        {
            var count = value.Count;
            for (var i = 0; i < count; i++)
            {
                var (column, cell) = value[i];
                writer.WritePropertyName(column);
                SqlRowsConverter.WriteValue(writer, cell);
            }
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
