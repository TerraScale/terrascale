using System.Buffers.Text;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Garbage collection (docs/terrabase/multi-region-replication.md section 11). A machine's <em>stable</em> stamp of a
/// database is the smallest <c>applied</c> stamp over the origins it heard from in the last 24 h (a row never advanced
/// counts as 0; see <see cref="LocalReplica.GoneBefore"/>), and its own clock; its <em>horizon</em> is the smallest stable
/// over itself and the peers heard from in the last 24 h (as they report it), less 60 s. Below the horizon every replica holds every event of every
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

    /// <summary>This machine's stable stamp of the database at <paramref name="now"/> (its clock).</summary>
    internal ValueTask<Hlc> GetStableAsync(Hlc now, CancellationToken cancellationToken) => RunAsync(() => Stable(now), cancellationToken);

    /// <summary>Records <paramref name="peer"/>'s reported stable stamp (uncaptured bookkeeping), and that it was heard from.</summary>
    internal async ValueTask RecordStableAsync(ReplicaId peer, Hlc stable, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        _ = await RunAsync(() =>
        {
            var update = Command("UPDATE _tb_peers SET stable = max(stable, $stable), seen = $seen WHERE node = $node;");
            update.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            update.Parameters.AddWithValue("$stable", Version(stable));
            update.Parameters.AddWithValue("$seen", now.ToUnixTimeMilliseconds());
            if (update.ExecuteNonQuery() == 0)
            {
                var insert = Command("INSERT INTO _tb_peers (node, applied, acked, stable, seen) VALUES ($node, 0, 0, $stable, $seen);");
                insert.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
                insert.Parameters.AddWithValue("$stable", Version(stable));
                insert.Parameters.AddWithValue("$seen", now.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    /// <summary>This machine's GC horizon at <paramref name="now"/>.</summary>
    internal ValueTask<Hlc> GetHorizonAsync(Hlc now, CancellationToken cancellationToken) => RunAsync(() => Horizon(now), cancellationToken);

    // Worker-only.
    private Hlc Stable(Hlc now)
    {
        var command = Command("SELECT min(applied) FROM _tb_peers WHERE seen >= $seen;");
        command.Parameters.AddWithValue("$seen", Member.GoneBefore(now.Milliseconds));
        return command.ExecuteScalar() is long applied ? new Hlc(Math.Min(now.Value, (ulong)applied)) : now;
    }

    // Worker-only.
    private Hlc Horizon(Hlc now)
    {
        var stable = Stable(now).Value;
        var command = Command("SELECT min(stable) FROM _tb_peers WHERE seen >= $seen;");
        command.Parameters.AddWithValue("$seen", Member.GoneBefore(now.Milliseconds));
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
                RemoveMerkleState(collection, id);
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

                // The documents it shared a unique key with are reprojected once it is gone: a hidden loser may now win.
                if (IsClient)
                {
                    BeginUniqueAffected();
                    Command("DELETE FROM _tb_unique_affected;").ExecuteNonQuery();
                    QueueUniqueContenders(collection, id);
                }

                RemoveMerkleState(collection, id);
                foreach (var sql in ForgetStatements)
                {
                    var delete = Command(sql, collection);
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                }

                if (IsClient)
                {
                    ReconcileUnique(collection, UniqueProjectionIds(cancellationToken), cancellationToken);
                }

                forgotten++;
            }

            return forgotten;
        }, cancellationToken, mutation: true);
}
