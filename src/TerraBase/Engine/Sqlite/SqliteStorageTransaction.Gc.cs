using System.Buffers.Text;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Garbage collection (docs/terrabase/multi-region-replication.md section 11). A machine's <em>stable</em> stamp of a
/// database is the smallest <c>applied</c> stamp over the origins it heard from in the last 24 h (a row never advanced
/// counts as 0), and its own clock; its <em>horizon</em> is the smallest stable over itself and the peers heard from in
/// the last 24 h (as their acks report it), less 60 s. Below the horizon every replica holds every event of every
/// origin, so a deleted document's state whose entries all lie below it can go.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    // 60 s in stamp units (48-bit milliseconds, 16-bit logical counter): the margin for writes minted but not yet committed.
    private const ulong HorizonMargin = 60_000UL << 16;

    private const int MaxCollected = 1024;

    private static readonly string[] ForgetStatements =
    [
        "DELETE FROM _tb_state WHERE collection = $collection AND id = $id;",
        "DELETE FROM _tb_unique_keys WHERE collection = $collection AND id = $id;",
        "DELETE FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id;",
        "DELETE FROM _tb_unique_invalid WHERE collection = $collection AND id = $id;",
    ];

    // A repair's horizon, read once per transaction.
    private Hlc? _repairHorizon;

    /// <summary>This machine's stable stamp of the database at <paramref name="now"/> (its clock).</summary>
    internal ValueTask<Hlc> GetStableAsync(Hlc now, CancellationToken cancellationToken) => RunAsync(() => Stable(now), cancellationToken);

    /// <summary>This machine's GC horizon at <paramref name="now"/>.</summary>
    internal ValueTask<Hlc> GetHorizonAsync(Hlc now, CancellationToken cancellationToken) => RunAsync(() => Horizon(now), cancellationToken);

    // Worker-only.
    private Hlc Stable(Hlc now)
    {
        var command = Command("SELECT min(applied) FROM _tb_peers WHERE seen >= $seen;");
        command.Parameters.AddWithValue("$seen", (long)now.Milliseconds - 86_400_000);
        return command.ExecuteScalar() is long applied ? new Hlc(Math.Min(now.Value, (ulong)applied)) : now;
    }

    // Worker-only.
    private Hlc Horizon(Hlc now)
    {
        var stable = Stable(now).Value;
        var command = Command("SELECT min(stable) FROM _tb_peers WHERE seen >= $seen;");
        command.Parameters.AddWithValue("$seen", (long)now.Milliseconds - 86_400_000);
        if (command.ExecuteScalar() is long reported)
        {
            stable = Math.Min(stable, (ulong)reported);
        }

        return new Hlc(stable > HorizonMargin ? stable - HorizonMargin : 0);
    }

    /// <summary>
    /// Deletes up to 1024 states of deleted documents whose every entry lies below <paramref name="horizon"/>, oldest first;
    /// returns how many.
    /// </summary>
    internal ValueTask<int> CollectAsync(Hlc horizon, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var candidates = Command("SELECT collection, id, state FROM _tb_state WHERE live = 0 AND stamp < $horizon ORDER BY stamp LIMIT $limit;");
            candidates.Parameters.AddWithValue("$horizon", Version(horizon));
            candidates.Parameters.AddWithValue("$limit", (long)MaxCollected);
            List<(string Collection, string Id)> dead = [];
            using (var reader = candidates.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The row's stamp is the last operation applied to it; a join of an older delta can leave newer entries.
                    var state = DocumentStateEncoding.DecodeStored(Base64Url.DecodeFromChars(reader.GetString(2)), []);
                    if (state.MaxStamp.Value < horizon.Value)
                    {
                        dead.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }
            }

            foreach (var (collection, id) in dead)
            {
                var delete = Command("DELETE FROM _tb_state WHERE collection = $collection AND id = $id AND live = 0;", collection);
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            return dead.Count;
        }, cancellationToken, mutation: true);

    /// <summary>
    /// A copying refill (section 10): deletes the local state and document of each of <paramref name="keys"/> whose every
    /// entry lies below <paramref name="horizon"/>, the horizon of the peer that no longer holds it (it was collected
    /// there). A newer state stays: the peer lacks it because it has not received it yet. Returns how many went.
    /// </summary>
    internal ValueTask<int> ForgetAsync(IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var forgotten = 0;
            foreach (var (collection, id) in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = ReadState(collection, id);
                if (state.MaxStamp.Value == 0 || state.MaxStamp.Value >= horizon.Value)
                {
                    continue;
                }

                if (HasTable(collection))
                {
                    PutRow(collection, id, null);
                }

                foreach (var sql in ForgetStatements)
                {
                    var delete = Command(sql, collection);
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                }

                forgotten++;
            }

            return forgotten;
        }, cancellationToken, mutation: true);

    // Worker-only: a repair (origin 0) never brings back a state below the horizon that this file lacks. Every replica
    // holds every event below the horizon, so a missing one was collected here.
    private bool CollectedHere(string collection, string id, DocumentState delta)
    {
        _repairHorizon ??= Horizon(Member.Clock.Tick());
        if (delta.MaxStamp.Value >= _repairHorizon.Value.Value)
        {
            return false;
        }

        var command = Command("SELECT 1 FROM _tb_state WHERE collection = $collection AND id = $id;", collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is null;
    }
}
