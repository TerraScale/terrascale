namespace TerraScale.Dashboard.Common.Accounts;

internal sealed record RowChanges<TRow>(IReadOnlyList<TRow> Added, IReadOnlyList<TRow> Changed, IReadOnlyList<TRow> Removed)
{
    public bool Any => Added.Count > 0 || Changed.Count > 0 || Removed.Count > 0;
}

internal static class RowSync
{
    public static RowChanges<TRow> Diff<TRow>(IEnumerable<TRow> loaded, IEnumerable<TRow> desired, Func<TRow, string> key) where TRow : class
    {
        var rows = new Dictionary<string, TRow>(StringComparer.Ordinal);
        foreach (var row in loaded)
        {
            rows.TryAdd(key(row), row);
        }

        List<TRow> added = [];
        List<TRow> changed = [];
        foreach (var next in desired)
        {
            if (!rows.Remove(key(next), out var row))
            {
                added.Add(next);
            }
            else if (!EqualityComparer<TRow>.Default.Equals(row, next))
            {
                changed.Add(next);
            }
        }

        return new(added, changed, [.. rows.Values]);
    }

    public static DateTimeOffset At(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
