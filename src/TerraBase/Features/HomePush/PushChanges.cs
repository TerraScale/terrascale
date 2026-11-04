using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.HomeApply;

namespace TerraBase.Features.HomePush;

internal sealed class LocalWriteRefusedException(string message) : InvalidOperationException(message);

internal static class PushChanges
{
    public static async ValueTask<List<RegionChange>> FromCapturedAsync(IReplicaWriter writer, IReadOnlyList<CapturedRowChange> captured, CancellationToken cancellationToken)
    {
        var keys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<RegionChange>(captured.Count);
        foreach (var change in captured)
        {
            if (!keys.TryGetValue(change.Table, out var key))
            {
                keys[change.Table] = key = await KeysAsync(writer, change.Table, cancellationToken).ConfigureAwait(false);
            }

            changes.Add(change.Type switch
            {
                CapturedRowChange.Insert => new RegionChange("insert", change.Table, null, Pick(change.Columns, change.After!, _ => true)),
                CapturedRowChange.Delete => new RegionChange("delete", change.Table, Pick(change.Columns, change.Before!, column => key.Contains(column, StringComparer.OrdinalIgnoreCase)), null),
                _ => Update(change, key),
            });
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
                    change.Values.Values.Select(ApplySql.Value).ToArray()),
                "update" => ($"UPDATE {table} SET {string.Join(", ", change.Values!.Keys.Select(column => ApplySql.Quote(column) + " = ?"))} WHERE {Where(change.Key!)}",
                    change.Values.Values.Concat(change.Key!.Values).Select(ApplySql.Value).ToArray()),
                _ => ($"DELETE FROM {table} WHERE {Where(change.Key!)}", change.Key!.Values.Select(ApplySql.Value).ToArray()),
            };
            _ = await writer.ExecuteAsync(ApplySql.Statement(sql, args), cancellationToken).ConfigureAwait(false);
        }
    }

    private static RegionChange Update(CapturedRowChange change, string[] key)
    {
        if (change.Columns.Where((column, index) => key.Contains(column, StringComparer.OrdinalIgnoreCase) && !Equals(change.Before![index], change.After![index])).Any())
        {
            throw new LocalWriteRefusedException("a primary key change commits at the database's home");
        }

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

    private static async ValueTask<string[]> KeysAsync(IReplicaWriter writer, string table, CancellationToken cancellationToken)
    {
        var info = await writer.ExecuteAsync(ApplySql.Statement($"PRAGMA table_info({ApplySql.Quote(table)})"), cancellationToken).ConfigureAwait(false);
        var keyed = info.Rows.Where(row => row[5] is long position && position > 0).OrderBy(row => (long)row[5]!).ToList();
        if (keyed.Count == 0)
        {
            throw new LocalWriteRefusedException("a table without a declared primary key commits at the database's home");
        }

        if (keyed is [var only] && string.Equals(only[2] as string, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalWriteRefusedException("a table keyed by its rowid commits at the database's home");
        }

        return [.. keyed.Select(row => (string)row[1]!)];
    }
}
