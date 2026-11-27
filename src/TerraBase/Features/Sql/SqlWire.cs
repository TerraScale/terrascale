using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using TerraBase.Engine;
using TerraScale.Contracts.Http;

namespace TerraBase.Features.Sql;

internal readonly union SqlStatementsParse(IReadOnlyList<SqlStatement>, SqlRefusal);

public sealed record SqlExecuteInput(string? Project, string? Database, IReadOnlyList<SqlHttpStatement>? Statements, string? Durability = null)
{
    [JsonIgnore]
    internal bool PreParsed { get; init; }

    [JsonIgnore]
    internal SqlStatement[]? ParsedStatements { get; init; }

    [JsonIgnore]
    internal SqlRefusal? ParseError { get; init; }
}

public sealed record SqlRequestInput(IReadOnlyList<SqlHttpStatement>? Statements)
{
    [JsonIgnore]
    internal bool PreParsed { get; init; }

    [JsonIgnore]
    internal SqlStatement[]? ParsedStatements { get; init; }

    [JsonIgnore]
    internal SqlRefusal? ParseError { get; init; }
}

public sealed record LegacyColumnBody(string Name, string? Decltype);

public sealed record LegacyResultBody(
    IReadOnlyList<LegacyColumnBody> Columns,
    [property: JsonConverter(typeof(SqlRowsConverter))] IReadOnlyList<IReadOnlyList<SqlValue>> Rows,
    [property: JsonPropertyName("rows_affected")] long RowsAffected,
    [property: JsonPropertyName("last_insert_rowid")] long? LastInsertRowid);

[JsonConverter(typeof(LegacyExecuteBodyConverter))]
public sealed record LegacyExecuteBody(IReadOnlyList<LegacyResultBody> Results)
{
    [JsonIgnore]
    internal IReadOnlyList<SqlResultSet>? RawResults { get; init; }
}

public sealed record ResultColumnBody(string Name, string? Type);

public sealed record ResultBody(
    IReadOnlyList<ResultColumnBody> Columns,
    [property: JsonConverter(typeof(SqlRowsConverter))] IReadOnlyList<IReadOnlyList<SqlValue>> Rows,
    long RowsAffected,
    long LastInsertRowId);

[JsonConverter(typeof(ExecuteBodyConverter))]
public sealed record ExecuteBody(IReadOnlyList<ResultBody> Results, string Bookmark, string Consistency)
{
    [JsonIgnore]
    internal IReadOnlyList<SqlResultSet>? RawResults { get; init; }
}

internal static class SqlWire
{
    private const int MaxCachedSqlUtf8Bytes = 4096;

    public static SqlStatementsParse Statements(IReadOnlyList<SqlHttpStatement>? statements)
    {
        if (statements is not { Count: > 0 and <= SqlPlan.MaxStatements })
        {
            return SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
        }

        var count = statements.Count;
        var converted = new SqlStatement[count];
        if (statements is SqlHttpStatement[] array)
        {
            for (var index = 0; index < array.Length; index++)
            {
                if (Statement(array[index], index) is not { } statement)
                {
                    return SqlRefusal.Invalid("arguments are null, finite numbers, booleans, strings or {\"base64\": …} blobs", index);
                }

                converted[index] = statement;
            }

            return converted;
        }

        for (var index = 0; index < count; index++)
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
            var argCount = rawArgs.Count;
            var positional = new SqlValue[argCount];
            if (rawArgs is JsonElement[] argArray)
            {
                for (var i = 0; i < argArray.Length; i++)
                {
                    if (Value(argArray[i]) is not { } value)
                    {
                        return null;
                    }

                    positional[i] = value;
                }
            }
            else if (rawArgs is List<JsonElement> argList)
            {
                var argSpan = CollectionsMarshal.AsSpan(argList);
                for (var i = 0; i < argSpan.Length; i++)
                {
                    if (Value(argSpan[i]) is not { } value)
                    {
                        return null;
                    }

                    positional[i] = value;
                }
            }
            else
            {
                for (var i = 0; i < argCount; i++)
                {
                    if (Value(rawArgs[i]) is not { } value)
                    {
                        return null;
                    }

                    positional[i] = value;
                }
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
            if (rawNamed is Dictionary<string, JsonElement> dict)
            {
                foreach (var (name, arg) in dict)
                {
                    if (Value(arg) is not { } value)
                    {
                        return null;
                    }

                    bound[name] = value;
                }
            }
            else
            {
                foreach (var (name, arg) in rawNamed)
                {
                    if (Value(arg) is not { } value)
                    {
                        return null;
                    }

                    bound[name] = value;
                }
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

    public static bool TryReadExecuteBody(
        ReadOnlySpan<byte> payload,
        out SqlStatement[]? statements,
        out SqlRefusal? error,
        out string? durability)
    {
        statements = null;
        error = null;
        durability = null;

        if (payload.StartsWith("\uFEFF"u8))
        {
            payload = payload[3..];
        }

        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var hasStatements = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                if (PropertyEquals(ref reader, "statements"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        statements = null;
                        error = null;
                        hasStatements = false;
                    }
                    else if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        if (!TryReadStatements(ref reader, out statements, out error))
                        {
                            return false;
                        }

                        hasStatements = true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (PropertyEquals(ref reader, "durability"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        durability = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        durability = reader.ValueTextEquals("local"u8) ? "local"
                            : reader.ValueTextEquals("all"u8) ? "all"
                            : reader.GetString();
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    reader.Skip();
                }
            }

            if (reader.Read())
            {
                return false;
            }

            if (!hasStatements)
            {
                statements = null;
                error = SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
            }

            return true;
        }
        catch (JsonException)
        {
            statements = null;
            error = null;
            durability = null;
            return false;
        }
    }

    internal static bool PropertyEquals(ref Utf8JsonReader reader, ReadOnlySpan<byte> lowerAsciiName)
    {
        if (reader.ValueTextEquals(lowerAsciiName))
        {
            return true;
        }

        if (!reader.HasValueSequence && reader.ValueSpan.Length == lowerAsciiName.Length)
        {
            var span = reader.ValueSpan;
            for (var i = 0; i < span.Length; i++)
            {
                var b = span[i];
                if (b is >= (byte)'A' and <= (byte)'Z')
                {
                    b = (byte)(b + 32);
                }

                if (b != lowerAsciiName[i])
                {
                    return false;
                }
            }

            return true;
        }

        return string.Equals(reader.GetString(), Encoding.ASCII.GetString(lowerAsciiName), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryReadStatements(
        ref Utf8JsonReader reader,
        out SqlStatement[]? statements,
        out SqlRefusal? error,
        (ulong Hash, byte[]? Utf8, string? Sql)[]? sqlCache = null)
    {
        statements = null;
        error = null;
        SqlStatement? first = null;
        List<SqlStatement>? list = null;
        var count = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                error ??= SqlRefusal.Invalid("arguments are null, finite numbers, booleans, strings or {\"base64\": …} blobs", count);
                count++;
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            if (!TryReadStatement(ref reader, sqlCache, out var statement, out var validArgs))
            {
                return false;
            }

            if (count >= SqlPlan.MaxStatements)
            {
                error ??= SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
                count++;
                continue;
            }

            if (statement is null || !validArgs)
            {
                error ??= SqlRefusal.Invalid("arguments are null, finite numbers, booleans, strings or {\"base64\": …} blobs", count);
            }
            else if (error is null)
            {
                if (count == 0)
                {
                    first = statement;
                }
                else
                {
                    if (list is null)
                    {
                        list = new List<SqlStatement>(4);
                        if (first is not null)
                        {
                            list.Add(first);
                        }
                    }

                    list.Add(statement);
                }
            }

            count++;
        }

        if (count is 0 or > SqlPlan.MaxStatements)
        {
            error = SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
            return true;
        }

        if (error is not null)
        {
            return true;
        }

        statements = list is not null ? [.. list] : first is not null ? [first] : [];
        return true;
    }

    private static bool TryReadStatement(
        ref Utf8JsonReader reader,
        (ulong Hash, byte[]? Utf8, string? Sql)[]? sqlCache,
        out SqlStatement? statement,
        out bool validArgs)
    {
        statement = null;
        validArgs = true;
        string? sql = null;
        IReadOnlyList<SqlValue> positional = [];
        IReadOnlyDictionary<string, SqlValue> named = SqlBuild.NoNamed;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            if (PropertyEquals(ref reader, "sql"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.Null)
                {
                    sql = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    sql = ReadCachedSql(ref reader, sqlCache);
                }
                else
                {
                    return false;
                }
            }
            else if (PropertyEquals(ref reader, "args"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.Null)
                {
                    positional = [];
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    if (!TryReadPositionalArgs(ref reader, out positional, out var argsOk))
                    {
                        return false;
                    }

                    if (!argsOk)
                    {
                        validArgs = false;
                    }
                }
                else
                {
                    return false;
                }
            }
            else if (PropertyEquals(ref reader, "named"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.Null)
                {
                    named = SqlBuild.NoNamed;
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    if (!TryReadNamedArgs(ref reader, out named, out var namedOk))
                    {
                        return false;
                    }

                    if (!namedOk)
                    {
                        validArgs = false;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (!reader.Read())
                {
                    return false;
                }

                reader.Skip();
            }
        }

        if (sql is not null && validArgs)
        {
            statement = new SqlStatement(sql, positional, named);
        }

        return true;
    }

    private static string? ReadCachedSql(ref Utf8JsonReader reader, (ulong Hash, byte[]? Utf8, string? Sql)[]? sqlCache)
    {
        if (sqlCache is null || reader.HasValueSequence)
        {
            return reader.GetString();
        }

        var raw = reader.ValueSpan;
        if (raw.IsEmpty)
        {
            return "";
        }

        if (raw.Length > MaxCachedSqlUtf8Bytes)
        {
            return reader.GetString();
        }

        var hash = HashUtf8(raw);
        ref var slot = ref sqlCache[(int)(hash & (ulong)(sqlCache.Length - 1))];
        if (slot.Hash == hash && slot.Utf8 is { } cachedUtf8 && raw.SequenceEqual(cachedUtf8) && slot.Sql is { } cachedSql)
        {
            return cachedSql;
        }

        var decoded = reader.GetString();
        if (decoded is not null)
        {
            slot = (hash, raw.ToArray(), decoded);
        }

        return decoded;
    }

    private static ulong HashUtf8(ReadOnlySpan<byte> bytes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        for (var i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= prime;
        }

        return hash == 0 ? 1UL : hash;
    }

    private static bool TryReadPositionalArgs(ref Utf8JsonReader reader, out IReadOnlyList<SqlValue> args, out bool valid)
    {
        args = [];
        valid = true;
        SqlValue first = default;
        SqlValue second = default;
        List<SqlValue>? list = null;
        var count = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var value = ReadSqlValue(ref reader, out var syntaxOk);
            if (!syntaxOk)
            {
                return false;
            }

            if (value is not { } parsed)
            {
                valid = false;
            }
            else if (valid)
            {
                if (count == 0)
                {
                    first = parsed;
                }
                else if (count == 1)
                {
                    second = parsed;
                }
                else
                {
                    if (list is null)
                    {
                        list = new List<SqlValue>(4) { first, second };
                    }

                    list.Add(parsed);
                }
            }

            count++;
        }

        if (valid)
        {
            args = count switch
            {
                0 => [],
                1 => [first],
                2 => [first, second],
                _ => list is not null ? [.. list] : [],
            };
        }

        return true;
    }

    private static bool TryReadNamedArgs(ref Utf8JsonReader reader, out IReadOnlyDictionary<string, SqlValue> named, out bool valid)
    {
        named = SqlBuild.NoNamed;
        valid = true;
        Dictionary<string, SqlValue>? map = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            var key = reader.GetString();
            if (!reader.Read())
            {
                return false;
            }

            var value = ReadSqlValue(ref reader, out var syntaxOk);
            if (!syntaxOk)
            {
                return false;
            }

            if (key is null || value is not { } parsed)
            {
                valid = false;
            }
            else if (valid)
            {
                map ??= new Dictionary<string, SqlValue>(StringComparer.Ordinal);
                map[key] = parsed;
            }
        }

        if (valid && map is { Count: > 0 })
        {
            named = map;
        }

        return true;
    }

    private static SqlValue? ReadSqlValue(ref Utf8JsonReader reader, out bool syntaxOk)
    {
        syntaxOk = true;
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return SqlValue.Null;
            case JsonTokenType.True:
                return SqlValue.FromIntegral(1);
            case JsonTokenType.False:
                return SqlValue.FromIntegral(0);
            case JsonTokenType.String:
                return reader.GetString() is { } text ? SqlValue.FromText(text) : null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer))
                {
                    return SqlValue.FromIntegral(integer);
                }

                return reader.TryGetDouble(out var real) && double.IsFinite(real) ? SqlValue.FromReal(real) : null;
            case JsonTokenType.StartObject:
                return ReadBlobObject(ref reader, out syntaxOk);
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                syntaxOk = false;
                return null;
        }
    }

    private static SqlValue? ReadBlobObject(ref Utf8JsonReader reader, out bool syntaxOk)
    {
        syntaxOk = true;
        SqlValue? blob = null;
        var propertyCount = 0;
        var valid = true;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                syntaxOk = false;
                return null;
            }

            var isBase64 = reader.ValueTextEquals("base64"u8);
            propertyCount++;
            if (!reader.Read())
            {
                syntaxOk = false;
                return null;
            }

            if (propertyCount == 1 && isBase64 && reader.TokenType == JsonTokenType.String)
            {
                if (reader.TryGetBytesFromBase64(out var decoded))
                {
                    blob = SqlValue.FromBlob(decoded);
                }
                else if (reader.GetString() is { } text)
                {
                    var buffer = new byte[(text.Length * 3 / 4) + 3];
                    if (Convert.TryFromBase64String(text, buffer, out var written))
                    {
                        blob = SqlValue.FromBlob(buffer.AsMemory(0, written));
                    }
                    else
                    {
                        valid = false;
                    }
                }
                else
                {
                    valid = false;
                }
            }
            else
            {
                valid = false;
                reader.Skip();
            }
        }

        return valid && propertyCount == 1 ? blob : null;
    }

    public static LegacyExecuteBody Legacy(IReadOnlyList<SqlResultSet> results) =>
        new([]) { RawResults = results };

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

public sealed class LegacyExecuteBodyConverter : JsonConverter<LegacyExecuteBody>
{
    public override LegacyExecuteBody Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("legacy execute responses are response-only");

    public override void Write(Utf8JsonWriter writer, LegacyExecuteBody value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var useRaw = options.Encoder is null && !writer.Options.Indented;
        writer.WriteStartObject();
        writer.WritePropertyName("results"u8);
        writer.WriteStartArray();
        if (value.RawResults is { } rawResults)
        {
            for (var i = 0; i < rawResults.Count; i++)
            {
                var result = rawResults[i];
                writer.WriteStartObject();
                writer.WritePropertyName("columns"u8);
                writer.WriteStartArray();
                var columns = result.Columns;
                for (var j = 0; j < columns.Count; j++)
                {
                    var column = columns[j];
                    writer.WriteStartObject();
                    writer.WriteString("name"u8, column.Name);
                    if (column.DeclaredType is { } decltype)
                    {
                        writer.WriteString("decltype"u8, decltype);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("rows"u8);
                SqlRowsConverter.WriteRows(writer, result.Rows, useRaw);
                writer.WriteNumber("rows_affected"u8, result.RowsAffected);
                writer.WriteNumber("last_insert_rowid"u8, result.LastInsertRowId);
                writer.WriteEndObject();
            }
        }
        else
        {
            var results = value.Results;
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                writer.WriteStartObject();
                writer.WritePropertyName("columns"u8);
                writer.WriteStartArray();
                var columns = result.Columns;
                for (var j = 0; j < columns.Count; j++)
                {
                    var column = columns[j];
                    writer.WriteStartObject();
                    writer.WriteString("name"u8, column.Name);
                    if (column.Decltype is { } decltype)
                    {
                        writer.WriteString("decltype"u8, decltype);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("rows"u8);
                SqlRowsConverter.WriteRows(writer, result.Rows, useRaw);
                writer.WriteNumber("rows_affected"u8, result.RowsAffected);
                if (result.LastInsertRowid is { } rowid)
                {
                    writer.WriteNumber("last_insert_rowid"u8, rowid);
                }

                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

public sealed class ExecuteBodyConverter : JsonConverter<ExecuteBody>
{
    public override ExecuteBody Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("execute responses are response-only");

    public override void Write(Utf8JsonWriter writer, ExecuteBody value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var useRaw = options.Encoder is null && !writer.Options.Indented;
        writer.WriteStartObject();
        writer.WritePropertyName("results"u8);
        writer.WriteStartArray();
        if (value.RawResults is { } rawResults)
        {
            for (var i = 0; i < rawResults.Count; i++)
            {
                var result = rawResults[i];
                writer.WriteStartObject();
                writer.WritePropertyName("columns"u8);
                writer.WriteStartArray();
                var columns = result.Columns;
                for (var j = 0; j < columns.Count; j++)
                {
                    var column = columns[j];
                    writer.WriteStartObject();
                    writer.WriteString("name"u8, column.Name);
                    if (column.DeclaredType is { } type)
                    {
                        writer.WriteString("type"u8, type);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("rows"u8);
                SqlRowsConverter.WriteRows(writer, result.Rows, useRaw);
                writer.WriteNumber("rowsAffected"u8, result.RowsAffected);
                writer.WriteNumber("lastInsertRowId"u8, result.LastInsertRowId);
                writer.WriteEndObject();
            }
        }
        else
        {
            var results = value.Results;
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                writer.WriteStartObject();
                writer.WritePropertyName("columns"u8);
                writer.WriteStartArray();
                var columns = result.Columns;
                for (var j = 0; j < columns.Count; j++)
                {
                    var column = columns[j];
                    writer.WriteStartObject();
                    writer.WriteString("name"u8, column.Name);
                    if (column.Type is { } type)
                    {
                        writer.WriteString("type"u8, type);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("rows"u8);
                SqlRowsConverter.WriteRows(writer, result.Rows, useRaw);
                writer.WriteNumber("rowsAffected"u8, result.RowsAffected);
                writer.WriteNumber("lastInsertRowId"u8, result.LastInsertRowId);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        if (value.Bookmark is { } bookmark)
        {
            writer.WriteString("bookmark"u8, bookmark);
        }

        if (value.Consistency is { } consistency)
        {
            writer.WriteString("consistency"u8, consistency);
        }

        writer.WriteEndObject();
    }
}

internal ref struct RawJsonBuffer
{
    private static readonly byte[] SafeAscii = BuildSafeAscii();

    private readonly IBufferWriter<byte>? _writer;
    private Span<byte> _buffer;
    private byte[]? _rented;
    private int _length;

    public RawJsonBuffer(Span<byte> initial)
    {
        _writer = null;
        _buffer = initial;
        _rented = null;
        _length = 0;
    }

    public RawJsonBuffer(IBufferWriter<byte> writer)
    {
        _writer = writer;
        _buffer = writer.GetSpan(256);
        _rented = null;
        _length = 0;
    }

    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer[.._length];

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer[_length..]);
        _length += value.Length;
    }

    public void WriteInt64(long value)
    {
        EnsureCapacity(24);
        if (Utf8Formatter.TryFormat(value, _buffer[_length..], out var written))
        {
            _length += written;
        }
    }

    public void WriteDouble(double value)
    {
        EnsureCapacity(32);
        if (value.TryFormat(_buffer[_length..], out var written, default, CultureInfo.InvariantCulture))
        {
            _length += written;
        }
    }

    public void WriteJsonString(string text)
    {
        var span = text.AsSpan();
        EnsureCapacity(span.Length + 2);
        _buffer[_length++] = (byte)'"';
        var safe = SafeAscii;
        var destination = _buffer[_length..];
        var i = 0;
        for (; i < span.Length; i++)
        {
            var c = span[i];
            if (c >= 128 || safe[c] == 0)
            {
                break;
            }

            destination[i] = (byte)c;
        }

        _length += i;
        if (i < span.Length)
        {
            WriteEscapedTail(span[i..]);
        }

        WriteByte((byte)'"');
    }

    public void WriteBlob(ReadOnlySpan<byte> blob)
    {
        var maxBase64 = blob.IsEmpty ? 0 : Base64.GetMaxEncodedToUtf8Length(blob.Length);
        EnsureCapacity(13 + maxBase64);
        "{\"base64\":\""u8.CopyTo(_buffer[_length..]);
        _length += 11;
        if (!blob.IsEmpty)
        {
            _ = Base64.EncodeToUtf8(blob, _buffer[_length..], out _, out var written);
            _length += written;
        }

        _buffer[_length++] = (byte)'"';
        _buffer[_length++] = (byte)'}';
    }

    public void Flush()
    {
        if (_writer is { } writer && _length > 0)
        {
            writer.Advance(_length);
            _length = 0;
            _buffer = default;
        }
    }

    public void Dispose()
    {
        if (_rented is { } rented)
        {
            _rented = null;
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void WriteEscapedTail(ReadOnlySpan<char> tail)
    {
        if (tail.Length <= 256)
        {
            Span<byte> utf8Stack = stackalloc byte[768];
            _ = Utf8.FromUtf16(tail, utf8Stack, out _, out var utf8Length, replaceInvalidSequences: true);
            WriteEncodedUtf8(utf8Stack[..utf8Length]);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(tail.Length));
        try
        {
            _ = Utf8.FromUtf16(tail, rented, out _, out var utf8Length, replaceInvalidSequences: true);
            WriteEncodedUtf8(rented.AsSpan(0, utf8Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void WriteEncodedUtf8(scoped ReadOnlySpan<byte> utf8)
    {
        var encoder = JavaScriptEncoder.Default;
        var source = utf8;
        while (!source.IsEmpty)
        {
            var required = Math.Max(Math.Min(source.Length * 6, 4096), 16);
            EnsureCapacity(required);
            var status = encoder.EncodeUtf8(source, _buffer[_length..], out var bytesConsumed, out var bytesWritten, isFinalBlock: true);
            _length += bytesWritten;
            source = source[bytesConsumed..];
            if (status != OperationStatus.DestinationTooSmall)
            {
                break;
            }
        }
    }

    private void EnsureCapacity(int additional)
    {
        if (_length + additional <= _buffer.Length)
        {
            return;
        }

        if (_writer is { } writer)
        {
            if (_length > 0)
            {
                writer.Advance(_length);
                _length = 0;
            }

            _buffer = writer.GetSpan(Math.Max(additional, 256));
            return;
        }

        var nextSize = Math.Max(_buffer.Length * 2, _length + additional);
        var next = ArrayPool<byte>.Shared.Rent(nextSize);
        _buffer[.._length].CopyTo(next);
        if (_rented is { } previous)
        {
            ArrayPool<byte>.Shared.Return(previous);
        }

        _rented = next;
        _buffer = next;
    }

    private static byte[] BuildSafeAscii()
    {
        var table = new byte[128];
        var encoder = JavaScriptEncoder.Default;
        for (var i = 0x20; i < 0x7F; i++)
        {
            if (!encoder.WillEncode(i) && i is not '"' and not '\\')
            {
                table[i] = 1;
            }
        }

        return table;
    }
}

public sealed class SqlRowsConverter : JsonConverter<IReadOnlyList<IReadOnlyList<SqlValue>>>
{
    private static readonly JsonEncodedText Base64Property = JsonEncodedText.Encode("base64"u8);

    public override IReadOnlyList<IReadOnlyList<SqlValue>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("rows are response-only");

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        WriteRows(writer, value, options.Encoder is null && !writer.Options.Indented);
    }

    public static void WriteRows(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        WriteRows(writer, value, !writer.Options.Indented);
    }

    public static void WriteRows(IBufferWriter<byte> writer, IReadOnlyList<IReadOnlyList<SqlValue>> value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new RawJsonBuffer(writer);
        WriteRows(ref buffer, value);
        buffer.Flush();
    }

    internal static void WriteRows(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value, bool useRaw)
    {
        if (useRaw)
        {
            WriteRawRows(writer, value);
            return;
        }

        WriteStructuredRows(writer, value);
    }

    internal static void WriteRows(ref RawJsonBuffer buffer, IReadOnlyList<IReadOnlyList<SqlValue>> value)
    {
        if (value.Count == 0)
        {
            buffer.Write("[]"u8);
            return;
        }

        buffer.WriteByte((byte)'[');
        if (value is List<IReadOnlyList<SqlValue>> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            for (var r = 0; r < span.Length; r++)
            {
                if (r > 0)
                {
                    buffer.WriteByte((byte)',');
                }

                WriteRawRow(ref buffer, span[r]);
            }
        }
        else if (value is IReadOnlyList<SqlValue>[] array)
        {
            for (var r = 0; r < array.Length; r++)
            {
                if (r > 0)
                {
                    buffer.WriteByte((byte)',');
                }

                WriteRawRow(ref buffer, array[r]);
            }
        }
        else
        {
            var count = value.Count;
            for (var r = 0; r < count; r++)
            {
                if (r > 0)
                {
                    buffer.WriteByte((byte)',');
                }

                WriteRawRow(ref buffer, value[r]);
            }
        }

        buffer.WriteByte((byte)']');
    }

    private static void WriteRawRows(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value)
    {
        if (value.Count == 0)
        {
            writer.WriteRawValue("[]"u8, skipInputValidation: true);
            return;
        }

        Span<byte> stack = stackalloc byte[1024];
        var buffer = new RawJsonBuffer(stack);
        try
        {
            WriteRows(ref buffer, value);
            writer.WriteRawValue(buffer.WrittenSpan, skipInputValidation: true);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static void WriteRawRow(ref RawJsonBuffer buffer, IReadOnlyList<SqlValue> row)
    {
        buffer.WriteByte((byte)'[');
        if (row is SqlValue[] array)
        {
            for (var c = 0; c < array.Length; c++)
            {
                if (c > 0)
                {
                    buffer.WriteByte((byte)',');
                }

                WriteRawCell(ref buffer, array[c]);
            }
        }
        else if (row is List<SqlValue> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            for (var c = 0; c < span.Length; c++)
            {
                if (c > 0)
                {
                    buffer.WriteByte((byte)',');
                }

                WriteRawCell(ref buffer, span[c]);
            }
        }
        else
        {
            var count = row.Count;
            for (var c = 0; c < count; c++)
            {
                if (c > 0)
                {
                    buffer.WriteByte((byte)',');
                }

                WriteRawCell(ref buffer, row[c]);
            }
        }

        buffer.WriteByte((byte)']');
    }

    private static void WriteRawCell(ref RawJsonBuffer buffer, SqlValue cell)
    {
        switch (cell.Kind)
        {
            case SqlValueKind.Null:
                buffer.Write("null"u8);
                break;
            case SqlValueKind.Integral:
                cell.TryGetIntegral(out var integer);
                buffer.WriteInt64(integer);
                break;
            case SqlValueKind.Real:
                cell.TryGetReal(out var real);
                buffer.WriteDouble(real);
                break;
            case SqlValueKind.Text:
                if (cell.TryGetText(out var text))
                {
                    buffer.WriteJsonString(text);
                }
                else
                {
                    buffer.Write("null"u8);
                }

                break;
            case SqlValueKind.Blob:
                cell.TryGetBlob(out var blob);
                buffer.WriteBlob(blob.Span);
                break;
            default:
                throw new UnreachableException();
        }
    }

    private static void WriteStructuredRows(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value)
    {
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
                writer.WriteBase64String(Base64Property, blob.Span);
                writer.WriteEndObject();
                break;
            default:
                throw new UnreachableException();
        }
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(LegacyExecuteBodyConverter), typeof(ExecuteBodyConverter)])]
[JsonSerializable(typeof(SqlExecuteInput))]
[JsonSerializable(typeof(SqlRequestInput))]
[JsonSerializable(typeof(LegacyExecuteBody))]
[JsonSerializable(typeof(ExecuteBody))]
internal sealed partial class SqlJsonContext : JsonSerializerContext;
