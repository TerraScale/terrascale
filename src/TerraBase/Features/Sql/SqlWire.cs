using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
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
    private static readonly JsonEncodedText Base64Property = JsonEncodedText.Encode("base64"u8);

    private static readonly byte[] SafeAscii = BuildSafeAscii();

    public override IReadOnlyList<IReadOnlyList<SqlValue>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("rows are response-only");

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (options.Encoder is null && !writer.Options.Indented)
        {
            WriteRawRows(writer, value);
            return;
        }

        WriteStructuredRows(writer, value);
    }

    public static void WriteRows(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<SqlValue>> value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (!writer.Options.Indented)
        {
            WriteRawRows(writer, value);
            return;
        }

        WriteStructuredRows(writer, value);
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

    private ref struct RawJsonBuffer(Span<byte> initial)
    {
        private Span<byte> _buffer = initial;
        private byte[]? _rented;
        private int _length;

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
                var encoded = JsonEncodedText.Encode(span[i..], JavaScriptEncoder.Default);
                Write(encoded.EncodedUtf8Bytes);
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

        public void Dispose()
        {
            if (_rented is { } rented)
            {
                _rented = null;
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void EnsureCapacity(int additional)
        {
            if (_length + additional <= _buffer.Length)
            {
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
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SqlExecuteInput))]
[JsonSerializable(typeof(SqlRequestInput))]
[JsonSerializable(typeof(LegacyExecuteBody))]
[JsonSerializable(typeof(ExecuteBody))]
internal sealed partial class SqlJsonContext : JsonSerializerContext;
