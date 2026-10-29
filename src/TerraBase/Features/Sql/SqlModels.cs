using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Errors;

namespace TerraBase.Features.Sql;

/// <summary>Statements to run as one transaction.</summary>
/// <param name="Statements">1 to 256 statements.</param>
internal sealed record SqlExecuteRequest(IReadOnlyList<SqlExecuteStatement>? Statements);

/// <summary>One statement and its arguments.</summary>
/// <param name="Sql">Statement text, at most 16 KiB.</param>
/// <param name="Args">Positional arguments: null, numbers, booleans (1 or 0) or strings.</param>
/// <param name="Named">Named arguments keyed without their <c>:</c>, <c>@</c> or <c>$</c> prefix.</param>
internal sealed record SqlExecuteStatement(string? Sql, IReadOnlyList<JsonElement>? Args = null, IReadOnlyDictionary<string, JsonElement>? Named = null);

/// <summary>One result per statement, in order.</summary>
/// <param name="Results">The results.</param>
internal sealed record SqlExecuteBody(IReadOnlyList<SqlStatementBody> Results);

/// <summary>A statement's result.</summary>
/// <param name="Columns">Result columns.</param>
/// <param name="Rows">Rows, each one value per column: null, a number or a string.</param>
/// <param name="RowsAffected">Rows a write changed.</param>
/// <param name="LastInsertRowid">The integer key of the last inserted row, when the table has one.</param>
internal sealed record SqlStatementBody(
    IReadOnlyList<SqlColumnBody> Columns,
    [property: JsonConverter(typeof(SqlRowsConverter))] IReadOnlyList<IReadOnlyList<SqlValue>> Rows,
    [property: JsonPropertyName("rows_affected")] long RowsAffected,
    [property: JsonPropertyName("last_insert_rowid")] long? LastInsertRowid);

/// <summary>A result column.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Decltype">Declared type of a table column, or null for an expression.</param>
internal sealed record SqlColumnBody(string Name, string? Decltype);

/// <summary>A refused statement: <c>{"error":{"code","message","statement","sqlite_code"}}</c>.</summary>
/// <param name="Error">The error.</param>
internal sealed record SqlErrorBody(SqlErrorDetail Error);

/// <summary>Why a statement failed.</summary>
/// <param name="Code"><c>sql_error</c>, <c>forbidden_scope</c>, <c>sql_conflict</c> or <c>sql_too_large</c>.</param>
/// <param name="Message">The rule that failed; never quotes the statement.</param>
/// <param name="Statement">Zero-based index of the failing statement.</param>
/// <param name="SqliteCode">The SQLite error code, such as <c>SQLITE_CONSTRAINT_PRIMARYKEY</c>.</param>
internal sealed record SqlErrorDetail(string Code, string Message, int Statement, [property: JsonPropertyName("sqlite_code")] string SqliteCode);

/// <summary>Converts JSON arguments to SQL values.</summary>
internal static class SqlJson
{
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
        foreach (var (name, arg) in statement.Named ?? new Dictionary<string, JsonElement>())
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

/// <summary>Writes rows of SQL values as JSON arrays; integers as numbers, so clients needing 64 bits use Hrana.</summary>
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
                    default:
                        writer.WriteNullValue();
                        break;
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
