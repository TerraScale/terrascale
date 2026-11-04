using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Clock;
using TerraBase.Features.Sql;

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

internal sealed class RowStamps
{
    public Dictionary<string, HlcTimestamp> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HlcTimestamp? Deleted { get; set; }

    public bool Stored { get; init; }

    public HlcTimestamp? Newest => Columns.Count == 0 ? null : Columns.Values.Max();

    public static RowStamps Read(string stamps, string? deleted)
    {
        var row = new RowStamps { Stored = true, Deleted = StampText.Parse(deleted) };
        using var document = JsonDocument.Parse(stamps);
        foreach (var column in document.RootElement.EnumerateObject())
        {
            if (StampText.Parse(column.Value.GetString()) is { } stamp)
            {
                row.Columns[column.Name] = stamp;
            }
        }

        return row;
    }

    public string Write()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (column, stamp) in Columns.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(column, StampText.Format(stamp));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
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
        if (named.Rows is not [[string name]] || TerraScale.TerraBase.Contracts.TerraBaseSqlNames.IsInternalTable(name))
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
        transaction.RunAsync("INSERT INTO _ts_rows (tbl, pk, stamps, deleted) VALUES (?, ?, ?, ?) ON CONFLICT (tbl, pk) DO UPDATE SET stamps = excluded.stamps, deleted = excluded.deleted", [table, key, stamps.Write(), stamps.Deleted is { } deleted ? StampText.Format(deleted) : null], cancellationToken);

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

    public static object? Value(JsonElement value) => SqlJson.Value(value) switch
    {
        SqlValue sql => SqlSession.Bound(sql),
        _ => throw new FormatException("a value is not a SQL value"),
    };
}
