using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Clock;

namespace TerraBase.Features.HomeApply;

internal sealed record Rejection(string Kind, string? Table, string? Key, string Message);

internal sealed class RejectedException(Rejection rejection) : Exception(rejection.Message)
{
    public Rejection Rejection { get; } = rejection;
}

internal sealed record ConflictRecord(string Kind, string State, string? Table, string? Key, string? Change, string Message)
{
    public HomeApplyConflict Public => new(Kind, Table, Key is null ? null : JsonElement.Parse(Key), Message);
}

internal sealed record ForeignKey(string Child, IReadOnlyList<string> From, string Parent, IReadOnlyList<string>? To);

internal sealed class ApplyRun(IStorageTransaction transaction, HlcTimestamp stamp, CancellationToken cancellationToken)
{
    private readonly Dictionary<string, TableShape?> _shapes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TableShape Shape, object?[] Key, object?[]? Before)> _touched = new(StringComparer.Ordinal);
    private List<ForeignKey>? _foreignKeys;

    public List<ConflictRecord> Superseded { get; } = [];

    public (string Table, string Key)? Current { get; private set; }

    public static object?[]? KeyValues(TableShape shape, IReadOnlyDictionary<string, JsonElement> source)
    {
        var values = new object?[shape.Keys.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var found = source.FirstOrDefault(pair => pair.Key.Equals(shape.Keys[i], StringComparison.OrdinalIgnoreCase));
            if (found.Key is null)
            {
                return null;
            }

            try
            {
                values[i] = ApplySql.Value(found.Value);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return values;
    }

    public async ValueTask ApplyAsync(RegionChange change)
    {
        Current = null;
        if (change.Op is not ("insert" or "update" or "delete"))
        {
            throw Reject("schema", change.Table, null, "a change is an insert, update or delete");
        }

        var shape = await ShapeAsync(change.Table!).ConfigureAwait(false)
            ?? throw Reject("schema", change.Table, null, "no such table");
        if (shape.Keys.Count == 0)
        {
            throw Reject("schema", shape.Name, null, "the table has no declared primary key");
        }

        var source = change.Op == "insert" ? change.Values : change.Key;
        if (source is null || KeyValues(shape, source) is not { } key)
        {
            throw Reject("schema", shape.Name, null, "the change does not name every primary-key column");
        }

        var keyText = ApplySql.KeyText(key);
        var rowKey = ApplySql.Object(shape.Keys, key);
        Current = (shape.Name, rowKey);
        var values = Values(shape, change, rowKey);
        var row = await ApplySql.ReadRowAsync(transaction, shape, key, cancellationToken).ConfigureAwait(false);
        _ = _touched.TryAdd(shape.Name + "\n" + keyText, (shape, key, row));
        var stamps = await ApplySql.ReadStampsAsync(transaction, shape.Name, keyText, cancellationToken).ConfigureAwait(false);
        switch (change.Op)
        {
            case "insert" when row is null:
                if (stamps.Deleted is { } deleted && deleted > stamp)
                {
                    Supersede(shape, rowKey, change.Op, values, "a newer delete of the row is kept");
                    return;
                }

                var columns = values.Keys.ToList();
                _ = await transaction.ExecuteStatementAsync(ApplySql.Statement(
                    $"INSERT INTO {ApplySql.Quote(shape.Name)} ({string.Join(", ", columns.Select(ApplySql.Quote))}) VALUES ({string.Join(", ", columns.Select(_ => "?"))})",
                    [.. columns.Select(column => values[column])]), cancellationToken).ConfigureAwait(false);
                stamps.Columns.Clear();
                foreach (var column in columns)
                {
                    stamps.Columns[column] = stamp;
                }

                stamps.Deleted = null;
                break;
            case "insert":
                throw Reject("primary_key", shape.Name, rowKey, "UNIQUE constraint failed: the row already exists at the home");
            case "update" when row is not null:
                await MergeAsync(shape, key, rowKey, change.Op, values, stamps).ConfigureAwait(false);
                break;
            case "update" when stamps.Deleted is not null:
                Supersede(shape, rowKey, change.Op, values, "the row was deleted at the home");
                return;
            case "update":
                throw Reject("missing_row", shape.Name, rowKey, "the row does not exist at the home");
            case "delete" when row is not null:
                if (stamps.Newest is { } newest && newest > stamp)
                {
                    Supersede(shape, rowKey, change.Op, values, "a newer write of the row is kept");
                    return;
                }

                _ = await transaction.ExecuteStatementAsync(ApplySql.Statement($"DELETE FROM {ApplySql.Quote(shape.Name)} WHERE {shape.KeyWhere}", key), cancellationToken)
                    .ConfigureAwait(false);
                stamps.Columns.Clear();
                stamps.Deleted = stamp;
                break;
            default:
                stamps.Deleted = stamps.Deleted is { } tombstone && tombstone > stamp ? tombstone : stamp;
                break;
        }

        _ = await ApplySql.WriteStampsAsync(transaction, shape.Name, keyText, stamps, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> Values(TableShape shape, RegionChange change, string rowKey)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (change.Op == "delete")
        {
            return change.Values is { Count: > 0 } ? throw Reject("schema", shape.Name, rowKey, "a delete carries only its key") : values;
        }

        if (change.Values is not { Count: > 0 } given)
        {
            throw Reject("schema", shape.Name, rowKey, "the change carries no values");
        }

        foreach (var (name, value) in given)
        {
            var column = shape.Column(name) ?? throw Reject("schema", shape.Name, rowKey, "no such column");
            if (change.Op == "update" && shape.Keys.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                throw Reject("schema", shape.Name, rowKey, "an update cannot change a primary-key column");
            }

            try
            {
                values[column] = ApplySql.Value(value);
            }
            catch (FormatException)
            {
                throw Reject("schema", shape.Name, rowKey, "a value is not a SQL value");
            }
        }

        return values;
    }

    private async ValueTask MergeAsync(TableShape shape, object?[] key, string rowKey, string op, Dictionary<string, object?> values, RowStamps stamps)
    {
        var won = new List<string>();
        var lost = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (column, value) in values)
        {
            if (shape.Keys.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (stamps.Columns.TryGetValue(column, out var current) && current > stamp)
            {
                lost[column] = value;
            }
            else
            {
                won.Add(column);
                stamps.Columns[column] = stamp;
            }
        }

        if (lost.Count > 0)
        {
            Supersede(shape, rowKey, op, lost, "a newer write of these columns is kept");
        }

        if (won.Count > 0)
        {
            _ = await transaction.ExecuteStatementAsync(ApplySql.Statement(
                $"UPDATE {ApplySql.Quote(shape.Name)} SET {string.Join(", ", won.Select(column => ApplySql.Quote(column) + " = ?"))} WHERE {shape.KeyWhere}",
                [.. won.Select(column => values[column]), .. key]), cancellationToken).ConfigureAwait(false);
        }

        stamps.Deleted = null;
    }

    private void Supersede(TableShape shape, string rowKey, string op, Dictionary<string, object?> values, string message)
    {
        var change = ApplySql.Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("op", op);
            writer.WriteStartObject("values");
            foreach (var (column, value) in values)
            {
                writer.WritePropertyName(column);
                ApplySql.WriteValue(writer, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        Superseded.Add(new ConflictRecord(WriteStates.Superseded, WriteStates.Superseded, shape.Name, rowKey, change, message));
    }

    public async ValueTask CheckForeignKeysAsync()
    {
        foreach (var (shape, key, before) in _touched.Values)
        {
            var row = await ApplySql.ReadRowAsync(transaction, shape, key, cancellationToken).ConfigureAwait(false);
            Current = (shape.Name, ApplySql.Object(shape.Keys, key));
            if (row is not null)
            {
                foreach (var foreignKey in (await ForeignKeysAsync().ConfigureAwait(false)).Where(foreignKey => foreignKey.Child.Equals(shape.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var values = Pick(shape, row, foreignKey.From);
                    if (values is null || values.Any(value => value is null) || await ShapeAsync(foreignKey.Parent).ConfigureAwait(false) is not { } parent)
                    {
                        continue;
                    }

                    if (!await ExistsAsync(parent.Name, foreignKey.To ?? parent.Keys, values).ConfigureAwait(false))
                    {
                        throw Reject("foreign_key", shape.Name, Current.Value.Key, "FOREIGN KEY constraint failed: the referenced row does not exist at the home");
                    }
                }
            }

            if (before is null)
            {
                continue;
            }

            foreach (var foreignKey in (await ForeignKeysAsync().ConfigureAwait(false)).Where(foreignKey => foreignKey.Parent.Equals(shape.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var referenced = foreignKey.To ?? shape.Keys;
                var old = Pick(shape, before, referenced);
                if (old is null || old.Any(value => value is null) || (row is not null && Pick(shape, row, referenced) is { } now && now.SequenceEqual(old)))
                {
                    continue;
                }

                if (!await ExistsAsync(shape.Name, referenced, old).ConfigureAwait(false) && await ExistsAsync(foreignKey.Child, foreignKey.From, old).ConfigureAwait(false))
                {
                    throw Reject("foreign_key", shape.Name, Current.Value.Key, "FOREIGN KEY constraint failed: a row still references it");
                }
            }
        }
    }

    private static object?[]? Pick(TableShape shape, object?[] row, IReadOnlyList<string> columns)
    {
        var values = new object?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var at = -1;
            for (var c = 0; c < shape.Columns.Count; c++)
            {
                if (shape.Columns[c].Equals(columns[i], StringComparison.OrdinalIgnoreCase))
                {
                    at = c;
                    break;
                }
            }

            if (at < 0)
            {
                return null;
            }

            values[i] = row[at];
        }

        return values;
    }

    private async ValueTask<bool> ExistsAsync(string table, IReadOnlyList<string> columns, object?[] values)
    {
        var where = string.Join(" AND ", columns.Select(column => ApplySql.Quote(column) + " = ?"));
        var found = await transaction.ExecuteStatementAsync(ApplySql.Statement($"SELECT 1 FROM {ApplySql.Quote(table)} WHERE {where} LIMIT 1", values), cancellationToken)
            .ConfigureAwait(false);
        return found.Rows.Count > 0;
    }

    private async ValueTask<List<ForeignKey>> ForeignKeysAsync()
    {
        if (_foreignKeys is not null)
        {
            return _foreignKeys;
        }

        _foreignKeys = [];
        var tables = await transaction.RunAsync("SELECT name FROM sqlite_schema WHERE type = 'table'", cancellationToken).ConfigureAwait(false);
        foreach (var row in tables.Rows)
        {
            if (row[0] is not string table || TerraScale.TerraBase.Contracts.TerraBaseSqlNames.IsInternalTable(table))
            {
                continue;
            }

            var list = await transaction.RunAsync($"PRAGMA foreign_key_list({ApplySql.Quote(table)})", cancellationToken).ConfigureAwait(false);
            foreach (var group in list.Rows.GroupBy(entry => (long)entry[0]!))
            {
                var ordered = group.OrderBy(entry => (long)entry[1]!).ToList();
                var to = ordered.Select(entry => entry[4] as string).ToList();
                _foreignKeys.Add(new ForeignKey(table, [.. ordered.Select(entry => (string)entry[3]!)], (string)ordered[0][2]!,
                    to.Any(column => column is null) ? null : [.. to.Select(column => column!)]));
            }
        }

        return _foreignKeys;
    }

    private async ValueTask<TableShape?> ShapeAsync(string table)
    {
        if (!_shapes.TryGetValue(table, out var shape))
        {
            _shapes[table] = shape = await ApplySql.ShapeAsync(transaction, table, cancellationToken).ConfigureAwait(false);
        }

        return shape;
    }

    private static RejectedException Reject(string kind, string? table, string? key, string message) => new(new Rejection(kind, table, key, message));
}
