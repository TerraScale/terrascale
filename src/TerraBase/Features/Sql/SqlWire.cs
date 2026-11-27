using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
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
    public static SqlStatementsParse Statements(IReadOnlyList<SqlHttpStatement>? statements)
    {
        if (statements is not { Count: > 0 and <= SqlPlan.MaxStatements })
        {
            return SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
        }

        var converted = new SqlStatement[statements.Count];
        for (var index = 0; index < statements.Count; index++)
        {
            if (Statement(statements[index], index) is not { } statement)
            {
                return SqlRefusal.Invalid("arguments are null, finite numbers, booleans, strings or {\"base64\": …} blobs", index);
            }

            converted[index] = statement;
        }

        return converted;
    }

    public static SqlStatement? Statement(SqlHttpStatement? statement, int index)
    {
        if (statement?.Sql is not { } sql || index < 0)
        {
            return null;
        }

        IReadOnlyList<SqlValue> args;
        if (statement.Args is { Count: > 0 } rawArgs)
        {
            var positional = new SqlValue[rawArgs.Count];
            for (var i = 0; i < rawArgs.Count; i++)
            {
                if (Value(rawArgs[i]) is not { } value)
                {
                    return null;
                }

                positional[i] = value;
            }

            args = positional;
        }
        else
        {
            args = [];
        }

        IReadOnlyDictionary<string, SqlValue> named;
        if (statement.Named is { Count: > 0 } rawNamed)
        {
            var bound = new Dictionary<string, SqlValue>(rawNamed.Count, StringComparer.Ordinal);
            foreach (var (name, arg) in rawNamed)
            {
                if (Value(arg) is not { } value)
                {
                    return null;
                }

                bound[name] = value;
            }

            named = bound;
        }
        else
        {
            named = SqlBuild.NoNamed;
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

    public static LegacyExecuteBody Legacy(IReadOnlyList<SqlResultSet> results)
    {
        if (results.Count == 0)
        {
            return new LegacyExecuteBody([]);
        }

        var converted = new LegacyResultBody[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            LegacyColumnBody[] columns;
            if (result.Columns.Count == 0)
            {
                columns = [];
            }
            else
            {
                columns = new LegacyColumnBody[result.Columns.Count];
                for (var j = 0; j < result.Columns.Count; j++)
                {
                    var column = result.Columns[j];
                    columns[j] = new LegacyColumnBody(column.Name, column.DeclaredType);
                }
            }

            converted[i] = new LegacyResultBody(columns, result.Rows, result.RowsAffected, result.LastInsertRowId);
        }

        return new LegacyExecuteBody(converted);
    }

    public static IReadOnlyList<ResultBody> Results(IReadOnlyList<SqlResultSet> results)
    {
        if (results.Count == 0)
        {
            return [];
        }

        var converted = new ResultBody[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            ResultColumnBody[] columns;
            if (result.Columns.Count == 0)
            {
                columns = [];
            }
            else
            {
                columns = new ResultColumnBody[result.Columns.Count];
                for (var j = 0; j < result.Columns.Count; j++)
                {
                    var column = result.Columns[j];
                    columns[j] = new ResultColumnBody(column.Name, column.DeclaredType);
                }
            }

            converted[i] = new ResultBody(columns, result.Rows, result.RowsAffected, result.LastInsertRowId);
        }

        return converted;
    }

    private static SqlValue? Blob(JsonElement value)
    {
        var properties = value.EnumerateObject();
        if (!properties.MoveNext())
        {
            return null;
        }

        var property = properties.Current;
        if (properties.MoveNext() || !property.NameEquals("base64"u8) || property.Value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (property.Value.TryGetBytesFromBase64(out var decoded))
        {
            return SqlValue.FromBlob(decoded);
        }

        if (property.Value.GetString() is not { } text)
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
        WriteRows(writer, value);
    }

    public static void WriteRows(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        if (value is List<IReadOnlyList<SqlValue>> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            for (var r = 0; r < span.Length; r++)
            {
                WriteRow(writer, span[r]);
            }
        }
        else if (value is IReadOnlyList<SqlValue>[] array)
        {
            for (var r = 0; r < array.Length; r++)
            {
                WriteRow(writer, array[r]);
            }
        }
        else
        {
            var count = value.Count;
            for (var r = 0; r < count; r++)
            {
                WriteRow(writer, value[r]);
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteRow(Utf8JsonWriter writer, IReadOnlyList<SqlValue> row)
    {
        writer.WriteStartArray();
        if (row is SqlValue[] array)
        {
            for (var c = 0; c < array.Length; c++)
            {
                WriteValue(writer, array[c]);
            }
        }
        else if (row is List<SqlValue> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            for (var c = 0; c < span.Length; c++)
            {
                WriteValue(writer, span[c]);
            }
        }
        else
        {
            var count = row.Count;
            for (var c = 0; c < count; c++)
            {
                WriteValue(writer, row[c]);
            }
        }

        writer.WriteEndArray();
    }

    public static void WriteValue(Utf8JsonWriter writer, SqlValue cell)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (cell.Kind)
        {
            case SqlValueKind.Null:
                writer.WriteNullValue();
                break;
            case SqlValueKind.Integral:
                cell.TryGetIntegral(out var integer);
                writer.WriteNumberValue(integer);
                break;
            case SqlValueKind.Real:
                cell.TryGetReal(out var real);
                writer.WriteNumberValue(real);
                break;
            case SqlValueKind.Text:
                if (cell.TryGetText(out var text))
                {
                    writer.WriteStringValue(text);
                }
                else
                {
                    writer.WriteNullValue();
                }

                break;
            case SqlValueKind.Blob:
                cell.TryGetBlob(out var blob);
                writer.WriteStartObject();
                writer.WriteBase64String("base64"u8, blob.Span);
                writer.WriteEndObject();
                break;
            default:
                throw new UnreachableException();
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SqlExecuteInput))]
[JsonSerializable(typeof(SqlRequestInput))]
[JsonSerializable(typeof(LegacyExecuteBody))]
[JsonSerializable(typeof(ExecuteBody))]
internal sealed partial class SqlJsonContext : JsonSerializerContext;
