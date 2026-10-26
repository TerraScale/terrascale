using System.Buffers.Text;
using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Garbage collection (docs/terrabase/multi-region-replication.md section 11). Stable is the smallest applied stamp
/// over retained origins and this machine's clock; horizon is the smallest retained stable stamp, less 60 s.
/// Eviction requires both silence and absence from the machine directory for 24 h, with startup protection.
/// Listed machines with unknown identities contribute zero, so a partition cannot release their GC hold.
/// </summary>
internal sealed partial class TursoStorageTransaction
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
    internal ValueTask<Hlc> GetStableAsync(Hlc now, CancellationToken cancellationToken, DatabasePlacement? placement = null) =>
        RunAsync(() => Bounds(now, placement).Stable, cancellationToken);

    /// <summary>Records <paramref name="peer"/>'s reported stable stamp (uncaptured bookkeeping), and that it was heard from.</summary>
    internal async ValueTask RecordStableAsync(ReplicaId peer, Hlc stable, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, peerProgressOnly: true).ConfigureAwait(false);
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
    internal ValueTask<Hlc> GetHorizonAsync(Hlc now, CancellationToken cancellationToken, DatabasePlacement? placement = null) =>
        RunAsync(() => Bounds(now, placement).Horizon, cancellationToken);

    // Worker-only.
    private (Hlc Stable, Hlc Horizon) Bounds(Hlc now, DatabasePlacement? placement)
    {
        var stable = now.Value;
        var reported = ulong.MaxValue;
        var progress = Member.Presence.TrackProgress(placement);
        using (var reader = Command("SELECT node, applied, stable, seen FROM _tb_peers;").ExecuteReader())
        {
            while (reader.Read())
            {
                var node = new ReplicaId(unchecked((ulong)reader.GetInt64(0)));
                if (progress.ContainsKey(node))
                {
                    progress[node] = true;
                }
                if (!Member.Gone(node, reader.GetInt64(3), now.Milliseconds))
                {
                    stable = Math.Min(stable, (ulong)reader.GetInt64(1));
                    reported = Math.Min(reported, (ulong)reader.GetInt64(2));
                }
            }
        }

        if (Member.Presence.HoldsMissingProgress(placement, progress))
        {
            (stable, reported) = (0, 0);
        }

        var horizon = Math.Min(stable, reported);
        return (new Hlc(stable), new Hlc(Math.Max(CollectedBefore().Value, horizon > HorizonMargin ? horizon - HorizonMargin : 0)));
    }

    /// <summary>The durable collected-state frontier, read in this transaction's snapshot.</summary>
    internal ValueTask<Hlc> GetCollectedBeforeAsync(CancellationToken cancellationToken) => RunAsync(CollectedBefore, cancellationToken);

    /// <summary>Inherits a certified source's frontier after baseline import and before side-copy salvage.</summary>
    internal async ValueTask InheritCollectedBeforeAsync(Hlc collectedBefore, CancellationToken cancellationToken)
    {
        _ = await RunAsync(() =>
        {
            AdvanceCollectedBefore(collectedBefore);
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // A returning peer may lower the current horizon, but cannot undo a committed collection.
    private Hlc CollectedBefore() => Command("SELECT CASE WHEN typeof(stamp) = 'integer' AND stamp >= 0 THEN stamp ELSE -1 END FROM _tb_gc_floor WHERE id = 1;")
        .ExecuteScalar() is long stamp && stamp >= 0
        ? new Hlc((ulong)stamp) : throw StoredDataCorruption.Create("the collected-state frontier is missing or invalid");

    private void AdvanceCollectedBefore(Hlc horizon)
    {
        var command = Command("UPDATE _tb_gc_floor SET stamp = max(stamp, $stamp) WHERE id = 1;");
        command.Parameters.AddWithValue("$stamp", Version(horizon));
        command.ExecuteNonQuery();
    }

    private static ClearEntry CollectedClear(Hlc horizon) =>
        new(new EntryVersion(new Hlc(horizon.Value - 1), new ReplicaId(ulong.MaxValue)), true, new Hlc(horizon.Value - 1));

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

            if (dead.Count > 0)
            {
                AdvanceCollectedBefore(horizon);
            }

            return dead.Count;
        }, cancellationToken, mutation: true);

    /// <summary>
    /// A copying refill (section 10): deletes the local state and document of each of <paramref name="keys"/> whose every
    /// entry lies below <paramref name="horizon"/>, the horizon of the peer that no longer holds it (it was collected
    /// there). A newer state stays: the peer lacks it because it has not received it yet. Returns how many went.
    /// </summary>
    internal ValueTask<int> ForgetAsync(IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
        RunAsync(() => Forget(keys, horizon, refill: false, cancellationToken), cancellationToken, mutation: true);

    internal ValueTask<int> ForgetRefillAsync(IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
        RunAsync(() => Forget(keys, horizon, refill: true, cancellationToken), cancellationToken, mutation: true);

    internal ValueTask<bool> CanForgetAsync(IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            foreach (var (collection, id) in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = ReadState(collection, id);
                // Source absence and age alone do not prove that a live local state was deleted.
                if (state.Exists && state.MaxStamp.Value < horizon.Value)
                {
                    return false;
                }
            }

            return true;
        }, cancellationToken);

    private int Forget(IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, bool refill, CancellationToken cancellationToken)
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

            var indexCollection = IsClient && collection == DocumentDelta.Indexes
                ? ReadIndexes(cancellationToken).Find(index => index.Name == id)?.Collection : null;
            if (HasTable(collection))
            {
                PutRow(collection, id, null);
            }

            var affected = !IsMetadata(collection) ? ReplaceUniqueKeys(collection, id, null, cancellationToken, local: false) : [];

            RemoveMerkleState(collection, id);
            foreach (var sql in ForgetStatements)
            {
                var delete = Command(sql, collection);
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            if (IsClient && collection == DocumentDelta.Indexes)
            {
                IndexDefinitionMetadata(id, null, cancellationToken);
            }
            else if (IsClient && collection == DocumentDelta.Unique)
            {
                ReprojectUnique(id, cancellationToken);
            }
            else if (!IsMetadata(collection))
            {
                ReconcileUnique(collection, affected, cancellationToken);
            }

            if (IsClient)
            {
                RemoveEmptyForgottenCollection(collection is DocumentDelta.Collections or DocumentDelta.Unique ? id : collection, cancellationToken);
                if (indexCollection is not null)
                {
                    RemoveEmptyForgottenCollection(indexCollection, cancellationToken);
                }
            }

            forgotten++;
        }

        if (!refill && forgotten > 0)
        {
            AdvanceCollectedBefore(horizon);
        }

        return forgotten;
    }

    private void RemoveEmptyForgottenCollection(string collection, CancellationToken cancellationToken)
    {
        if (IsMetadata(collection) || !HasTable(collection) || ReadState(DocumentDelta.Collections, collection).MaxStamp.Value != 0)
        {
            return;
        }

        // Remaining keys can be valid baseline survivors, even when older than the absence frontier.
        if (Command("SELECT 1 FROM _tb_state WHERE collection = $collection LIMIT 1;", collection).ExecuteScalar() is not null)
        {
            return;
        }

        if (ReadIndexes(cancellationToken).Any(index => index.Collection == collection)
            || Materialize(ReadState(DocumentDelta.Unique, collection)) is not null)
        {
            return;
        }

        DropTable(collection);
        DropCollectionRow(collection);
        DropUniqueCollection(collection, local: false);
        ChangedReaderState = true;
    }
}
