using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Contracts.Http;

namespace TerraBase.Features.Sql;

internal readonly union SqlStatementsParse(IReadOnlyList<SqlStatement>, SqlRefusal);

public sealed record SqlExecuteInput(string? Project, string? Database, IReadOnlyList<SqlHttpStatement>? Statements, string? Durability = null);

public sealed record SqlRequestInput(IReadOnlyList<SqlHttpStatement>? Statements);

public sealed record LegacyColumnBody(string Name, string? Decltype);

public sealed record LegacyResultBody(
    IReadOnlyList<LegacyColumnBody> Columns,
    [property: JsonConverter(typeof(SqlRowsConverter))] IReadOnlyList<IReadOnlyList<SqlValue>> Rows,
    [property: JsonPropertyName("rows_affected")] long RowsAffected,
    [property: JsonPropertyName("last_insert_rowid")] long? LastInsertRowid);

public sealed record LegacyExecuteBody(IReadOnlyList<LegacyResultBody> Results);

public sealed record ResultColumnBody(string Name, string? Type);

public sealed record ResultBody(
    IReadOnlyList<ResultColumnBody> Columns,
    [property: JsonConverter(typeof(SqlRowsConverter))] IReadOnlyList<IReadOnlyList<SqlValue>> Rows,
    long RowsAffected,
    long LastInsertRowId);

public sealed record ExecuteBody(IReadOnlyList<ResultBody> Results, string Bookmark, string Consistency);

internal static class SqlWire
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoNamed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public static SqlStatementsParse Statements(IReadOnlyList<SqlHttpStatement>? statements)
    {
        if (statements is not { Count: > 0 and <= SqlPlan.MaxStatements })
        {
            return SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
        }

        List<SqlStatement> converted = [];
        for (var index = 0; index < statements.Count; index++)
        {
            if (Statement(statements[index], index) is not { } statement)
            {
                return SqlRefusal.Invalid("arguments are null, finite numbers, booleans, strings or {\"base64\": …} blobs", index);
            }

            converted.Add(statement);
        }

        return converted;
    }

    public static SqlStatement? Statement(SqlHttpStatement? statement, int index)
    {
        if (statement?.Sql is not { } sql || index < 0)
        {
            return null;
        }

        List<SqlValue> args = [];
        foreach (var arg in statement.Args ?? [])
        {
            if (Value(arg) is not { } value)
            {
                return null;
            }

            args.Add(value);
        }

        Dictionary<string, SqlValue> named = new(StringComparer.Ordinal);
        foreach (var (name, arg) in statement.Named ?? NoNamed)
        {
            if (Value(arg) is not { } value)
            {
                return null;
            }

            named[name] = value;
        }

        return new SqlStatement(sql, args, named);
    }

    public static SqlValue? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => SqlValue.Null,
        JsonValueKind.True => SqlValue.FromIntegral(1),
        JsonValueKind.False => SqlValue.FromIntegral(0),
        JsonValueKind.String when value.GetString() is { } text => SqlValue.FromText(text),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => SqlValue.FromIntegral(integer),
        JsonValueKind.Number when value.TryGetDouble(out var real) && double.IsFinite(real) => SqlValue.FromReal(real),
        JsonValueKind.Object => Blob(value),
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Array or JsonValueKind.Undefined => null,
        _ => throw new UnreachableException(),
    };

    public static LegacyExecuteBody Legacy(IReadOnlyList<SqlResultSet> results) =>
        new([.. results.Select(static result => new LegacyResultBody(
            [.. result.Columns.Select(static column => new LegacyColumnBody(column.Name, column.DeclaredType))],
            result.Rows, result.RowsAffected, result.LastInsertRowId))]);

    public static IReadOnlyList<ResultBody> Results(IReadOnlyList<SqlResultSet> results) =>
        [.. results.Select(static result => new ResultBody(
            [.. result.Columns.Select(static column => new ResultColumnBody(column.Name, column.DeclaredType))],
            result.Rows, result.RowsAffected, result.LastInsertRowId))];

    private static SqlValue? Blob(JsonElement value)
    {
        if (!value.TryGetProperty("base64", out var encoded) || encoded.ValueKind != JsonValueKind.String || value.EnumerateObject().Count() != 1)
        {
            return null;
        }

        if (encoded.GetString() is not { } text)
        {
            return null;
        }

        var buffer = new byte[(text.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(text, buffer, out var written) ? SqlValue.FromBlob(buffer.AsMemory(0, written)) : null;
    }
}

public sealed class SqlRowsConverter : JsonConverter<IReadOnlyList<IReadOnlyList<SqlValue>>>
{
    public override IReadOnlyList<IReadOnlyList<SqlValue>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("rows are response-only");

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        foreach (var row in value)
        {
            writer.WriteStartArray();
            foreach (var cell in row)
            {
                WriteValue(writer, cell);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }

    public static void WriteValue(Utf8JsonWriter writer, SqlValue cell)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (cell.TryGetIntegral(out var integer))
        {
            writer.WriteNumberValue(integer);
        }
        else if (cell.TryGetReal(out var real))
        {
            writer.WriteNumberValue(real);
        }
        else if (cell.TryGetText(out var text))
        {
            writer.WriteStringValue(text);
        }
        else if (cell.TryGetBlob(out var blob))
        {
            writer.WriteStartObject();
            writer.WriteBase64String("base64", blob.Span);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SqlExecuteInput))]
[JsonSerializable(typeof(SqlRequestInput))]
[JsonSerializable(typeof(LegacyExecuteBody))]
[JsonSerializable(typeof(ExecuteBody))]
internal sealed partial class SqlJsonContext : JsonSerializerContext;
