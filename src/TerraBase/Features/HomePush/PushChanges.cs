using System.Diagnostics;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Sql;

namespace TerraBase.Features.HomePush;

internal readonly union TableKeys(string[], LocalWriteRefused);

internal readonly union CapturedChanges(IReadOnlyList<RegionChange>, LocalWriteRefused);

internal static class PushChanges
{
    public static async ValueTask<CapturedChanges> FromCapturedAsync(IReplicaWriter writer, IReadOnlyList<CapturedRowChange> captured, CancellationToken cancellationToken)
    {
        var keys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<RegionChange>(captured.Count);
        foreach (var change in captured)
        {
            if (!keys.TryGetValue(change.Table, out var key))
            {
                switch (await KeysAsync(writer, change.Table, cancellationToken).ConfigureAwait(false))
                {
                    case LocalWriteRefused refused:
                        return refused;
                    case string[] columns:
                        keys[change.Table] = key = columns;
                        break;
                    default:
                        throw new UnreachableException();
                }
            }

            RegionChange? built = change.Type switch
            {
                CapturedRowChange.Insert => new RegionChange("insert", change.Table, null, Pick(change.Columns, change.After!, _ => true)),
                CapturedRowChange.Delete => new RegionChange("delete", change.Table, Pick(change.Columns, change.Before!, column => key.Contains(column, StringComparer.OrdinalIgnoreCase)), null),
                _ => Update(change, key),
            };
            if (built is null)
            {
                return new LocalWriteRefused("a primary key change commits at the database's home");
            }

            changes.Add(built);
        }

        return changes;
    }

    public static async ValueTask ApplyAsync(IReplicaWriter writer, IReadOnlyList<RegionChange> changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            var table = ApplySql.Quote(change.Table!);
            var (sql, args) = change.Op switch
            {
                "insert" => ($"INSERT OR REPLACE INTO {table} ({string.Join(", ", change.Values!.Keys.Select(ApplySql.Quote))}) VALUES ({string.Join(", ", change.Values.Select(_ => "?"))})",
                    (object?[])[.. change.Values.Values.Select(Value)]),
                "update" => ($"UPDATE {table} SET {string.Join(", ", change.Values!.Keys.Select(column => ApplySql.Quote(column) + " = ?"))} WHERE {Where(change.Key!)}",
                    (object?[])[.. change.Values.Values.Concat(change.Key!.Values).Select(Value)]),
                _ => ($"DELETE FROM {table} WHERE {Where(change.Key!)}", [.. change.Key!.Values.Select(Value)]),
            };
            _ = await writer.ExecuteAsync(ApplySql.Statement(sql, args), cancellationToken).ConfigureAwait(false);
        }
    }

    private static object? Value(JsonElement value) => SqlJson.Value(value) is SqlValue sql ? SqlSession.Bound(sql) : throw new UnreachableException("a captured value is not a SQL value");

    private static RegionChange? Update(CapturedRowChange change, string[] key)
    {
        var updated = change.Updated;
        return new RegionChange("update", change.Table,
            Pick(change.Columns, change.After!, column => key.Contains(column, StringComparer.OrdinalIgnoreCase)),
            Pick(change.Columns, change.After!, (column, index) => !key.Contains(column, StringComparer.OrdinalIgnoreCase)
                && (updated is null ? !Equals(change.Before![index], change.After![index]) : updated[index])));
    }

    private static string Where(IReadOnlyDictionary<string, JsonElement> key) => string.Join(" AND ", key.Keys.Select(column => ApplySql.Quote(column) + " = ?"));

    private static Dictionary<string, JsonElement> Pick(IReadOnlyList<string> columns, object?[] values, Func<string, bool> include) =>
        Pick(columns, values, (column, _) => include(column));

    private static Dictionary<string, JsonElement> Pick(IReadOnlyList<string> columns, object?[] values, Func<string, int, bool> include)
    {
        var picked = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            if (include(columns[i], i))
            {
                using var document = JsonDocument.Parse(ApplySql.Json(json => ApplySql.WriteValue(json, i < values.Length ? values[i] : null)));
                picked[columns[i]] = document.RootElement.Clone();
            }
        }

        return picked;
    }

    private static async ValueTask<TableKeys> KeysAsync(IReplicaWriter writer, string table, CancellationToken cancellationToken)
    {
        var info = await writer.ExecuteAsync(ApplySql.Statement($"PRAGMA table_info({ApplySql.Quote(table)})"), cancellationToken).ConfigureAwait(false);
        var keyed = info.Rows.Where(row => row[5] is long position && position > 0).OrderBy(row => (long)row[5]!).ToList();
        if (keyed.Count == 0)
        {
            return new LocalWriteRefused("a table without a declared primary key commits at the database's home");
        }

        if (keyed is [var only] && string.Equals(only[2] as string, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalWriteRefused("a table keyed by its rowid commits at the database's home");
        }

        return new TableKeys([.. keyed.Select(row => (string)row[1]!)]);
    }
}
