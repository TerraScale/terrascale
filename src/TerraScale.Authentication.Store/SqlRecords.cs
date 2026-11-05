using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Store;

public sealed record StoredRow(string Id, JsonElement Body, string Version);

public sealed record StoredPage(IReadOnlyList<StoredRow> Items, string? NextCursor, long? Count = null);

public sealed record StoreSort(string Path, string? Order = null);

public sealed record StoreWrite(string Id, string Version);

public sealed class StorePreconditionFailedException(int operationIndex)
    : Exception("A conditional write found the record changed.")
{
    public int OperationIndex { get; } = operationIndex;
}

public sealed class StoreCursorException() : Exception("The cursor does not belong to this query.");

internal sealed record SqlWrite(string Op, string Table, string Id, JsonElement? Body = null, string? IfMatch = null, JsonElement? Filter = null)
{
    public static SqlWrite Create(string table, JsonElement rowData, string id) => new("create", table, id, rowData);

    public static SqlWrite Put(string table, string id, JsonElement rowData, string ifMatch) => new("put", table, id, rowData, ifMatch);

    public static SqlWrite Delete(string table, string id, string ifMatch) => new("delete", table, id, IfMatch: ifMatch);

    public static SqlWrite AssertAbsent(string table, string id) => new("absent", table, id);

    public static SqlWrite Assert(string table, string id, JsonElement filter) => new("assert", table, id, Filter: filter);
}

internal sealed record CursorPosition(
    [property: JsonPropertyName("d")] string Digest,
    [property: JsonPropertyName("v")] JsonElement Value,
    [property: JsonPropertyName("i")] string Id);

[JsonSerializable(typeof(CursorPosition))]
internal sealed partial class SqlRecordsJson : JsonSerializerContext;

internal sealed class SqlRecords(TerraBaseSqlDatabase database)
{
    public const string ChecksTable = "store_checks";

    private const int MaxStatements = TerraBaseSqlStatement.MaxPerRequest;

    public static IEnumerable<TerraBaseSqlStatement> Schema(IEnumerable<string> tables)
    {
        yield return new($"CREATE TABLE IF NOT EXISTS {ChecksTable} (id TEXT PRIMARY KEY NOT NULL, failed INTEGER NOT NULL)");
        foreach (var name in tables)
        {
            var table = Table(name);
            yield return new($"CREATE TABLE IF NOT EXISTS {table} (id TEXT PRIMARY KEY NOT NULL CHECK (length(id) BETWEEN 1 AND 128), instance_id TEXT, version TEXT NOT NULL CHECK (length(version) = 26), body TEXT NOT NULL CHECK (json_valid(body)))");
            yield return new($"CREATE INDEX IF NOT EXISTS {Table(name + "_instance")} ON {table} (instance_id, id)");
        }
    }

    public async Task<IReadOnlySet<string>> TablesAsync(CancellationToken cancellationToken)
    {
        var rows = await database.QueryAsync("SELECT name FROM sqlite_schema WHERE type = 'table'", cancellationToken: cancellationToken).ConfigureAwait(false);
        return rows.Select(row => row.GetString(0)).ToHashSet(StringComparer.Ordinal);
    }

    public Task ProvisionAsync(IEnumerable<string> tables, CancellationToken cancellationToken) =>
        database.BatchAsync([.. Schema(tables)], cancellationToken);

    public async Task<StoredRow?> GetAsync(string table, string id, CancellationToken cancellationToken)
    {
        var rows = await database.QueryAsync($"SELECT id, body, version FROM {Table(table)} WHERE id = ?", [id], cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : Read(rows[0]);
    }

    public async Task<StoredPage> QueryAsync(string table, string? instanceId, JsonElement? filter, IReadOnlyList<StoreSort>? sort, int limit, string? cursor, bool count, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A page holds 1 to 1000 records.");
        }

        if (sort is { Count: > 1 })
        {
            throw new ArgumentException("A query sorts by at most one field.", nameof(sort));
        }

        var args = new List<TerraBaseSqlValue>();
        var where = new StringBuilder("1 = 1");
        if (instanceId is not null)
        {
            where.Append(" AND instance_id = ?");
            args.Add(instanceId);
        }

        if (filter is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } condition)
        {
            where.Append(" AND ").Append(Condition(condition, args));
        }

        var key = sort is [{ } first] ? first : null;
        var descending = key?.Order switch
        {
            null or "asc" => false,
            "desc" => true,
            _ => throw new ArgumentException("A sort order is asc or desc.", nameof(sort)),
        };
        var sortExpression = key is null ? "NULL" : "json_extract(body, ?)";
        var sortPath = key is null ? null : JsonPath(key.Path);
        var digest = Digest(table, where.ToString(), args, key?.Path, descending);
        long? total = null;
        if (count)
        {
            var counted = await database.QueryAsync($"SELECT count(*) FROM {Table(table)} WHERE {where}", args, cancellationToken).ConfigureAwait(false);
            total = counted[0].GetInt64(0);
        }

        var pageArgs = new List<TerraBaseSqlValue>();
        if (sortPath is not null)
        {
            pageArgs.Add(sortPath);
        }

        pageArgs.AddRange(args);
        var page = new StringBuilder(where.ToString());
        if (cursor is not null)
        {
            var position = DecodeCursor(cursor, digest);
            page.Append(" AND ").Append(After(position, sortPath, descending, pageArgs));
        }

        var direction = descending ? "DESC" : "ASC";
        var order = sortPath is null ? $"id {direction}" : $"s {direction}, id {direction}";
        pageArgs.Add(limit + 1);
        var rows = await database.QueryAsync(
            $"SELECT id, body, version, {sortExpression} AS s FROM {Table(table)} WHERE {page} ORDER BY {order} LIMIT ?", pageArgs, cancellationToken).ConfigureAwait(false);
        var items = rows.Take(limit).Select(Read).ToList();
        string? next = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            next = EncodeCursor(new CursorPosition(digest, last["s"].Clone(), last.GetString("id")));
        }

        return new StoredPage(items, next, total);
    }

    public async Task<IReadOnlyList<string>?> WriteAsync(IReadOnlyList<SqlWrite> operations, Action<int>? failed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var request = AuthStore.NewId();
        var guards = new List<TerraBaseSqlStatement>();
        var writes = new List<TerraBaseSqlStatement>();
        var versions = new string[operations.Count];
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var table = Table(operation.Table);
            switch (operation.Op)
            {
                case "create":
                    versions[index] = AuthStore.NewId();
                    guards.Add(Guard(request, index, $"NOT EXISTS (SELECT 1 FROM {table} WHERE id = ?)", [operation.Id]));
                    writes.Add(new($"INSERT INTO {table} (id, instance_id, version, body) SELECT ?, ?, ?, ? WHERE {Passed}",
                        [operation.Id, InstanceOf(operation.Body!.Value), versions[index], operation.Body!.Value.GetRawText(), request]));
                    break;
                case "put":
                    versions[index] = AuthStore.NewId();
                    guards.Add(Guard(request, index, $"EXISTS (SELECT 1 FROM {table} WHERE id = ? AND version = ?)", [operation.Id, operation.IfMatch]));
                    writes.Add(new($"UPDATE {table} SET instance_id = ?, version = ?, body = ? WHERE id = ? AND {Passed}",
                        [InstanceOf(operation.Body!.Value), versions[index], operation.Body!.Value.GetRawText(), operation.Id, request]));
                    break;
                case "delete":
                    guards.Add(Guard(request, index, $"EXISTS (SELECT 1 FROM {table} WHERE id = ? AND version = ?)", [operation.Id, operation.IfMatch]));
                    writes.Add(new($"DELETE FROM {table} WHERE id = ? AND {Passed}", [operation.Id, request]));
                    break;
                case "absent":
                    guards.Add(Guard(request, index, $"NOT EXISTS (SELECT 1 FROM {table} WHERE id = ?)", [operation.Id]));
                    break;
                case "assert":
                    var args = new List<TerraBaseSqlValue> { operation.Id };
                    var condition = Condition(operation.Filter!.Value, args);
                    guards.Add(Guard(request, index, $"EXISTS (SELECT 1 FROM {table} WHERE id = ? AND {condition})", args));
                    break;
                default:
                    throw new ArgumentException($"Unknown operation {operation.Op}.", nameof(operations));
            }
        }

        if (guards.Count == 0 && writes.Count == 0)
        {
            return versions;
        }

        List<TerraBaseSqlStatement> statements =
        [
            new($"INSERT INTO {ChecksTable} (id, failed) VALUES (?, -1)", [request]),
            .. guards,
            .. writes,
            new($"SELECT failed FROM {ChecksTable} WHERE id = ?", [request]),
            new($"DELETE FROM {ChecksTable} WHERE id = ?", [request]),
        ];
        if (statements.Count > MaxStatements)
        {
            throw new InvalidOperationException("A store transaction exceeds TerraBase's statement limit.");
        }

        var results = await database.BatchAsync(statements, cancellationToken).ConfigureAwait(false);
        var outcome = results[^2].Rows is [var row] ? row.GetInt64(0) : throw new InvalidOperationException("TerraBase returned no write outcome.");
        if (outcome < 0)
        {
            return versions;
        }

        failed?.Invoke((int)outcome);
        return null;
    }

    private const string Passed = "EXISTS (SELECT 1 FROM " + ChecksTable + " WHERE id = ? AND failed = -1)";

    private static TerraBaseSqlStatement Guard(string request, int operation, string condition, IReadOnlyList<TerraBaseSqlValue> args) =>
        new($"UPDATE {ChecksTable} SET failed = ? WHERE id = ? AND failed = -1 AND NOT ({condition})", [operation, request, .. args]);

    public static string Table(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || name.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')) || !char.IsAsciiLetterLower(name[0]))
        {
            throw new ArgumentException("Table names are lowercase snake_case.", nameof(name));
        }

        return "\"" + name + "\"";
    }

    private static StoredRow Read(TerraBaseSqlRecord row)
    {
        using var body = JsonDocument.Parse(row.GetString("body"));
        return new StoredRow(row.GetString("id"), body.RootElement.Clone(), row.GetString("version"));
    }

    private static string? InstanceOf(JsonElement rowData) =>
        rowData.ValueKind == JsonValueKind.Object && rowData.TryGetProperty("instanceId", out var instance) && instance.ValueKind == JsonValueKind.String
            ? instance.GetString()
            : null;

    internal static string JsonPath(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/' || pointer.Length > 256)
        {
            throw new ArgumentException("A field is a JSON Pointer.", nameof(pointer));
        }

        var path = new StringBuilder("$");
        foreach (var segment in pointer[1..].Split('/'))
        {
            if (segment.Length == 0 || segment.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            {
                throw new ArgumentException("A field segment is letters, digits, '_' or '-'.", nameof(pointer));
            }

            path.Append(".\"").Append(segment).Append('"');
        }

        return path.ToString();
    }

    private static string Field(string pointer, List<TerraBaseSqlValue> args)
    {
        if (pointer == "/instanceId")
        {
            return "instance_id";
        }

        if (pointer == "$id")
        {
            return "id";
        }

        args.Add(JsonPath(pointer));
        return "json_extract(body, ?)";
    }

    internal static string Condition(JsonElement filter, List<TerraBaseSqlValue> args)
    {
        if (filter.ValueKind != JsonValueKind.Object || filter.EnumerateObject().Count() != 1)
        {
            throw new ArgumentException("A filter is an object with one operator.", nameof(filter));
        }

        var member = filter.EnumerateObject().First();
        var value = member.Value;
        switch (member.Name)
        {
            case "and" or "or":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                {
                    throw new ArgumentException($"{member.Name} takes a non-empty array.", nameof(filter));
                }

                var parts = value.EnumerateArray().Select(part => Condition(part, args)).ToList();
                return "(" + string.Join(member.Name == "and" ? " AND " : " OR ", parts) + ")";
            case "not":
                return "NOT " + Condition(value, args);
            case "exists":
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException("exists takes a field.", nameof(filter));
                }

                if (value.GetString() == "/instanceId")
                {
                    return "(instance_id IS NOT NULL)";
                }

                args.Add(JsonPath(value.GetString()!));
                return "(json_type(body, ?) IS NOT NULL)";
            case "eq":
            case "ne":
                var (path, operand) = Pair(value);
                var equal = Equal(path, operand, args);
                return member.Name == "eq" ? equal : "NOT " + equal;
            case "lt" or "lte" or "gt" or "gte":
                var (ordered, bound) = Pair(value);
                var symbol = member.Name switch { "lt" => "<", "lte" => "<=", "gt" => ">", _ => ">=" };
                var field = Field(ordered, args);
                args.Add(Scalar(bound, ordered: true));
                return $"coalesce({field} {symbol} ?, 0)";
            case "in" or "nin":
                var (listed, list) = Pair(value);
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                {
                    throw new ArgumentException($"{member.Name} takes a non-empty array of values.", nameof(filter));
                }

                var any = "(" + string.Join(" OR ", list.EnumerateArray().Select(item => Equal(listed, item, args))) + ")";
                return member.Name == "in" ? any : "NOT " + any;
            default:
                throw new ArgumentException($"Unsupported filter operator {member.Name}.", nameof(filter));
        }
    }

    private static (string Path, JsonElement Operand) Pair(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2 && value[0].ValueKind == JsonValueKind.String
            ? (value[0].GetString()!, value[1])
            : throw new ArgumentException("A comparison is [field, value].", nameof(value));

    private static string Equal(string path, JsonElement operand, List<TerraBaseSqlValue> args)
    {
        if (operand.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (path == "/instanceId")
            {
                return "0";
            }

            args.Add(JsonPath(path));
            return operand.ValueKind == JsonValueKind.True ? "coalesce(json_type(body, ?) = 'true', 0)" : "coalesce(json_type(body, ?) = 'false', 0)";
        }

        var field = Field(path, args);
        if (operand.ValueKind == JsonValueKind.Null)
        {
            return $"({field} IS NULL)";
        }

        args.Add(Scalar(operand, ordered: false));
        return $"coalesce({field} = ?, 0)";
    }

    private static TerraBaseSqlValue Scalar(JsonElement value, bool ordered) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True when !ordered => 1,
        JsonValueKind.False when !ordered => 0,
        _ => throw new ArgumentException("A comparison value is a string or a number.", nameof(value)),
    };

    private static string After(CursorPosition position, string? sortPath, bool descending, List<TerraBaseSqlValue> args)
    {
        var next = descending ? "<" : ">";
        if (sortPath is null)
        {
            args.Add(position.Id);
            return $"id {next} ?";
        }

        var s = "json_extract(body, ?)";
        if (position.Value.ValueKind == JsonValueKind.Null)
        {
            args.Add(sortPath);
            if (descending)
            {
                args.Add(position.Id);
                return $"({s} IS NULL AND id < ?)";
            }

            args.Add(sortPath);
            args.Add(position.Id);
            return $"({s} IS NOT NULL OR ({s} IS NULL AND id > ?))";
        }

        var value = Scalar(position.Value, ordered: true);
        args.Add(sortPath);
        args.Add(value);
        args.Add(sortPath);
        args.Add(value);
        args.Add(position.Id);
        if (descending)
        {
            args.Add(sortPath);
            return $"({s} < ? OR ({s} = ? AND id < ?) OR {s} IS NULL)";
        }

        return $"({s} > ? OR ({s} = ? AND id > ?))";
    }

    private static string Digest(string table, string where, IReadOnlyList<TerraBaseSqlValue> args, string? sort, bool descending)
    {
        var text = new StringBuilder(table).Append('\n').Append(where).Append('\n').Append(sort).Append('\n').Append(descending);
        foreach (var arg in args)
        {
            text.Append('\n').Append(arg.Value switch
            {
                null => "null",
                string value => "s" + value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value,
                IFormattable value => "n" + value.ToString(null, CultureInfo.InvariantCulture),
                var value => "o" + value,
            });
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private static string EncodeCursor(CursorPosition position) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(position, SqlRecordsJson.Default.CursorPosition)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CursorPosition DecodeCursor(string cursor, string digest)
    {
        try
        {
            var text = cursor.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            var position = JsonSerializer.Deserialize(Convert.FromBase64String(text), SqlRecordsJson.Default.CursorPosition);
            return position is not null && position.Digest == digest && !string.IsNullOrEmpty(position.Id)
                && position.Value.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number
                ? position
                : throw new StoreCursorException();
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            throw new StoreCursorException();
        }
    }
}
