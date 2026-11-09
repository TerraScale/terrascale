using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Errors;

namespace TerraBase.Features.Sql;

internal sealed record SqlExecuteRequest(IReadOnlyList<SqlExecuteStatement>? Statements, string? Durability = null);

internal sealed record SqlExecuteStatement(string? Sql, IReadOnlyList<JsonElement>? Args = null, IReadOnlyDictionary<string, JsonElement>? Named = null);

internal sealed record SqlExecuteBody(IReadOnlyList<SqlStatementBody> Results);

internal sealed record SqlStatementBody(
    IReadOnlyList<SqlColumnBody> Columns,
    [property: JsonConverter(typeof(SqlRowsConverter))] IReadOnlyList<IReadOnlyList<SqlValue>> Rows,
    [property: JsonPropertyName("rows_affected")] long RowsAffected,
    [property: JsonPropertyName("last_insert_rowid")] long? LastInsertRowid)
{
    public static SqlStatementBody Of(SqlResult result) => new(
        [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid);
}

internal sealed record SqlColumnBody(string Name, string? Decltype);

internal sealed record SqlErrorBody(SqlErrorDetail Error);

internal sealed record SqlErrorDetail(
    string Code,
    string Message,
    int Statement,
    string Sqlstate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocsUrl = null);

internal static class SqlJson
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoNamed = new Dictionary<string, JsonElement>();

    public static Valid<SqlValue> Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => SqlValue.Null,
        JsonValueKind.True => SqlValue.Of(1L),
        JsonValueKind.False => SqlValue.Of(0L),
        JsonValueKind.String => SqlValue.Of(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => SqlValue.Of(integer),
        JsonValueKind.Number when value.TryGetDouble(out var real) && double.IsFinite(real) => SqlValue.Of(real),
        JsonValueKind.Object when Blob(value) is { } blob => SqlValue.Of(blob),
        _ => new Validation("arguments are null, finite numbers, booleans, strings or {\"base64\": …} blobs"),
    };

    private static byte[]? Blob(JsonElement value)
    {
        if (!value.TryGetProperty("base64", out var encoded) || encoded.ValueKind != JsonValueKind.String || value.EnumerateObject().Count() != 1)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(encoded.GetString()!);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static Valid<SqlCommand> Command(SqlExecuteStatement statement)
    {
        var args = new List<SqlValue>(statement.Args?.Count ?? 0);
        foreach (var arg in statement.Args ?? [])
        {
            switch (Value(arg))
            {
                case Validation error:
                    return error;
                case SqlValue value:
                    args.Add(value);
                    break;
            }
        }

        var named = new Dictionary<string, SqlValue>(StringComparer.Ordinal);
        foreach (var (name, arg) in statement.Named ?? NoNamed)
        {
            switch (Value(arg))
            {
                case Validation error:
                    return error;
                case SqlValue value:
                    named[name] = value;
                    break;
            }
        }

        return new SqlCommand(statement.Sql ?? "", args, named);
    }
}

internal sealed class SqlRowsConverter : JsonConverter<IReadOnlyList<IReadOnlyList<SqlValue>>>
{
    public override IReadOnlyList<IReadOnlyList<SqlValue>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("rows are response-only");

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var row in value)
        {
            writer.WriteStartArray();
            foreach (var cell in row)
            {
                switch (cell.Kind)
                {
                    case SqlValueKind.Null:
                        writer.WriteNullValue();
                        break;
                    case SqlValueKind.Integer:
                        writer.WriteNumberValue(cell.Integer);
                        break;
                    case SqlValueKind.Real:
                        writer.WriteNumberValue(cell.Real);
                        break;
                    case SqlValueKind.Text:
                        writer.WriteStringValue(cell.Text);
                        break;
                    case SqlValueKind.Blob:
                        writer.WriteStartObject();
                        writer.WriteBase64String("base64", cell.Blob);
                        writer.WriteEndObject();
                        break;
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
