using TerraBase.Engine;
using TerraBase.Features.HomeApply;

namespace TerraBase.Features.Sync;

internal sealed record StreamSequence(string Source, ulong Sequence);

internal static class Frontier
{
    internal const string Ddl = "CREATE TABLE IF NOT EXISTS _ts_frontier (source TEXT PRIMARY KEY, seq INTEGER NOT NULL CHECK (seq >= 0))";

    public static async ValueTask<IReadOnlyDictionary<string, ulong>> ReadAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        var present = await transaction.RunAsync("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = '_ts_frontier'", cancellationToken).ConfigureAwait(false);
        var frontier = new Dictionary<string, ulong>(StringComparer.Ordinal);
        if (present.Rows.Count == 0)
        {
            return frontier;
        }

        var result = await transaction.RunAsync("SELECT source, seq FROM _ts_frontier", cancellationToken).ConfigureAwait(false);
        foreach (var row in result.Rows)
        {
            frontier.Add((string)row[0]!, checked((ulong)(long)row[1]!));
        }

        return frontier;
    }

    public static async ValueTask<bool> BehindAsync(IStorageTransaction transaction, StreamSequence position, CancellationToken cancellationToken) =>
        !(await ReadAsync(transaction, cancellationToken).ConfigureAwait(false)).TryGetValue(position.Source, out var reached) || reached < position.Sequence;

    public static async ValueTask AdvanceAsync(IStorageTransaction transaction, StreamSequence position, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(position.Source);
        ArgumentOutOfRangeException.ThrowIfZero(position.Sequence);
        _ = await transaction.RunAsync(Ddl, cancellationToken).ConfigureAwait(false);
        _ = await transaction.RunAsync(
            "INSERT INTO _ts_frontier (source, seq) VALUES (?, ?) ON CONFLICT (source) DO UPDATE SET seq = max(seq, excluded.seq)",
            [position.Source, checked((long)position.Sequence)], cancellationToken).ConfigureAwait(false);
    }
}
