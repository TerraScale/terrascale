using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Clock;
using TerraBase.Features.Sql;
using TerraScale.TerraBase.Contracts;

namespace TerraBase.Features.HomeApply;

internal static class StampText
{
    public static string Format(HlcTimestamp stamp) =>
        string.Create(CultureInfo.InvariantCulture, $"{stamp.PhysicalMilliseconds:x12}.{stamp.Logical:x8}:{stamp.NodeId}");

    public static HlcTimestamp? Parse(string? text) =>
        text is { Length: > 22 } && text[12] == '.' && text[21] == ':'
        && long.TryParse(text.AsSpan(0, 12), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var milliseconds)
        && uint.TryParse(text.AsSpan(13, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var logical)
            ? new HlcTimestamp(milliseconds, logical, text[22..])
            : null;
}

internal sealed record Writer(HlcTimestamp Stamp, string Region, string Transaction)
{
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(StampText.Format(Stamp));
        writer.WriteStringValue(Region);
        writer.WriteStringValue(Transaction);
        writer.WriteEndArray();
    }

    public static Writer Read(JsonElement element) =>
        element is { ValueKind: JsonValueKind.Array } && element.GetArrayLength() == 3 && StampText.Parse(element[0].GetString()) is { } stamp
            ? new Writer(stamp, element[1].GetString()!, element[2].GetString()!)
            : throw new InvalidDataException("a row stamp is damaged");
}

internal sealed class RowStamps
{
    public Dictionary<string, Writer> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Writer? Deleted { get; set; }

    public Writer? Newest => Columns.Count == 0 ? null : Columns.Values.MaxBy(writer => writer.Stamp);

    public static RowStamps Read(string stamps, string? deleted)
    {
        var row = new RowStamps();
        if (deleted is not null)
        {
            using var tombstone = JsonDocument.Parse(deleted);
            row.Deleted = Writer.Read(tombstone.RootElement);
        }

        using var document = JsonDocument.Parse(stamps);
        foreach (var column in document.RootElement.EnumerateObject())
        {
            row.Columns[column.Name] = Writer.Read(column.Value);
        }

        return row;
    }

    public string Write() => ApplySql.Json(writer =>
    {
        writer.WriteStartObject();
        foreach (var (column, stamp) in Columns.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(column);
            stamp.WriteTo(writer);
        }

        writer.WriteEndObject();
    });

    public string? WriteDeleted() => Deleted is { } deleted ? ApplySql.Json(deleted.WriteTo) : null;
}

internal sealed record TableShape(string Name, IReadOnlyList<string> Columns, IReadOnlyList<string> Keys)
{
    public string? Column(string name) => Columns.FirstOrDefault(column => column.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string KeyWhere => string.Join(" AND ", Keys.Select(key => $"{ApplySql.Quote(key)} = ?"));
}

internal static class ApplySql
{
    private static readonly IReadOnlyDictionary<string, object?> NoNamed = new Dictionary<string, object?>();

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static SqlStatement Statement(string sql, params object?[] args) =>
        new(sql, args, NoNamed, SqlSession.MaxRows, SqlSession.MaxBytes, SqlLimits.MaxRequestTime);

    public static ValueTask<SqlStatementResult> RunAsync(this IStorageTransaction transaction, string sql, CancellationToken cancellationToken) =>
        transaction.ExecuteStatementAsync(Statement(sql), cancellationToken);

    public static ValueTask<SqlStatementResult> RunAsync(this IStorageTransaction transaction, string sql, object?[] args, CancellationToken cancellationToken) =>
        transaction.ExecuteStatementAsync(Statement(sql, args), cancellationToken);

    public static async ValueTask<TableShape?> ShapeAsync(IStorageTransaction transaction, string table, CancellationToken cancellationToken)
    {
        var named = await transaction.RunAsync("SELECT name FROM sqlite_schema WHERE type = 'table' AND name = ? COLLATE NOCASE", [table], cancellationToken).ConfigureAwait(false);
        if (named.Rows is not [[string name]] || TerraBaseSqlNames.IsInternalTable(name))
        {
            return null;
        }

        var info = await transaction.RunAsync($"PRAGMA table_info({Quote(name)})", cancellationToken).ConfigureAwait(false);
        var columns = info.Rows.Select(row => (string)row[1]!).ToList();
        var keys = info.Rows.Where(row => row[5] is long position && position > 0).OrderBy(row => (long)row[5]!).Select(row => (string)row[1]!).ToList();
        return new TableShape(name, columns, keys);
    }

    public static async ValueTask<object?[]?> ReadRowAsync(IStorageTransaction transaction, TableShape shape, object?[] key, CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", shape.Columns.Select(Quote));
        var found = await transaction.ExecuteStatementAsync(Statement($"SELECT {columns} FROM {Quote(shape.Name)} WHERE {shape.KeyWhere}", key), cancellationToken)
            .ConfigureAwait(false);
        return found.Rows.Count == 0 ? null : found.Rows[0];
    }

    public static async ValueTask<RowStamps> ReadStampsAsync(IStorageTransaction transaction, string table, string key, CancellationToken cancellationToken)
    {
        var found = await transaction.RunAsync("SELECT stamps, deleted FROM _ts_rows WHERE tbl = ? AND pk = ?", [table, key], cancellationToken).ConfigureAwait(false);
        return found.Rows is [[string stamps, var deleted]] ? RowStamps.Read(stamps, deleted as string) : new RowStamps();
    }

    public static ValueTask<SqlStatementResult> WriteStampsAsync(IStorageTransaction transaction, string table, string key, RowStamps stamps, CancellationToken cancellationToken) =>
        transaction.RunAsync("INSERT INTO _ts_rows (tbl, pk, stamps, deleted) VALUES (?, ?, ?, ?) ON CONFLICT (tbl, pk) DO UPDATE SET stamps = excluded.stamps, deleted = excluded.deleted", [table, key, stamps.Write(), stamps.WriteDeleted()], cancellationToken);

    public static string KeyText(IEnumerable<object?> values) => Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            WriteValue(writer, value);
        }

        writer.WriteEndArray();
    });

    public static string Object(IReadOnlyList<string> names, IReadOnlyList<object?> values) => Json(writer =>
    {
        writer.WriteStartObject();
        for (var i = 0; i < names.Count; i++)
        {
            writer.WritePropertyName(names[i]);
            WriteValue(writer, values[i]);
        }

        writer.WriteEndObject();
    });

    public static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double real when double.IsFinite(real):
                writer.WriteRawValue(real == Math.Floor(real) && Math.Abs(real) < 1e15
                    ? real.ToString("0.0", CultureInfo.InvariantCulture)
                    : real.ToString("R", CultureInfo.InvariantCulture));
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
                writer.WriteNullValue();
                break;
        }
    }

    internal const int MaxShownBytes = 4096;

    public static string Shown(string json)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        return bytes <= MaxShownBytes ? json : Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
            writer.WriteNumber("bytes", bytes);
            writer.WriteEndObject();
        });
    }

    public static string ValueJson(object? value) => Shown(Json(writer => WriteValue(writer, value)));

    public static object? Value(JsonElement value) => SqlJson.Value(value) switch
    {
        SqlValue sql => SqlSession.Bound(sql),
        _ => throw new FormatException("a value is not a SQL value"),
    };
}
