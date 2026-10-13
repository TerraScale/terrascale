using System.Buffers.Text;
using System.Collections.Immutable;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Garbage collection (docs/terrabase/multi-region-replication.md section 11). A machine's <em>stable</em> stamp of a
/// database is the smallest <c>applied</c> stamp over the origins it has not evicted (a row never advanced counts as 0),
/// and its own clock; its <em>horizon</em> is the smallest stable over itself and the peers it has not evicted (as they
/// report it), less 60 s. A peer is evicted only once it has been silent and missing from the machine directory for 24 h
/// (<see cref="LocalReplica.Gone"/>): a region that keeps running behind a partition holds the horizon, however long.
/// Below the horizon every replica it holds holds every event of every origin, so a deleted document's state whose
/// entries all lie below it can go.
/// </summary>
/// <remarks>
/// Collection forgets that a document was deleted. Whatever later reaches this file from below the highest horizon it
/// collected at (<see cref="CollectedBelow"/>) for a document it no longer holds may be older than that deletion: a
/// returning machine's queued writes, a peer's state the deletion never reached, a copy restored from before it. Such a
/// state joins only where this file never held the document, or where it held a version newer than any collection (so it
/// lost the document rather than collected it); otherwise only its entries above that horizon join, and a delta that
/// cannot be judged is refused until its sender ships it as a whole state.
/// </remarks>
internal sealed partial class SqliteStorageTransaction
{
    // 60 s in stamp units (48-bit milliseconds, 16-bit logical counter): the margin for writes minted but not yet committed.
    private const ulong HorizonMargin = 60_000UL << 16;

    private const int MaxCollected = 1024;

    // _tb_peers row 0 (no replica id is 0) keeps the file's collection watermark in `applied`: the highest horizon below
    // which it dropped states. Its `acked` never holds back CDC retention, and its `seen` never makes it a peer heard from.
    private const string ReadCollected = "SELECT applied FROM _tb_peers WHERE node = 0;";

    private const string RaiseCollected = "UPDATE _tb_peers SET applied = max(applied, $applied) WHERE node = 0;";

    private const string InsertCollected = "INSERT INTO _tb_peers (node, applied, acked, stable, seen) VALUES (0, $applied, 9223372036854775807, 0, 0);";

    private static readonly string[] ForgetStatements =
    [
        "DELETE FROM _tb_state WHERE collection = $collection AND id = $id;",
        "DELETE FROM _tb_unique_keys WHERE collection = $collection AND id = $id;",
        "DELETE FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id;",
        "DELETE FROM _tb_unique_invalid WHERE collection = $collection AND id = $id;",
    ];

    // Read once per transaction, kept current by its own writes.
    private Hlc? _collectedBelow;
    private Dictionary<ulong, ulong>? _appliedByMachine;

    /// <summary>This machine's stable stamp of the database at <paramref name="now"/> (its clock).</summary>
    internal ValueTask<Hlc> GetStableAsync(Hlc now, CancellationToken cancellationToken) => RunAsync(() => Bounds(now).Stable, cancellationToken);

    /// <summary>This machine's GC horizon at <paramref name="now"/>.</summary>
    internal ValueTask<Hlc> GetHorizonAsync(Hlc now, CancellationToken cancellationToken) => RunAsync(() => Bounds(now).Horizon, cancellationToken);

    /// <summary>The highest horizon below which this file dropped states (0 when it never did).</summary>
    internal ValueTask<Hlc> GetCollectedBelowAsync(CancellationToken cancellationToken) => RunAsync(CollectedBelow, cancellationToken);

    /// <summary>
    /// What a peer refilled from this file may forget and adopt (section 10): <see cref="GetCollectedBelowAsync"/>, and every
    /// origin's applied stamp here, this machine's own as <paramref name="now"/>.
    /// </summary>
    internal ValueTask<(Hlc CollectedBelow, Dictionary<ReplicaId, Hlc> Applied)> GetRefillBasisAsync(Hlc now, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var applied = new Dictionary<ReplicaId, Hlc> { [Member.Id] = now };
            using (var reader = Command("SELECT node, applied FROM _tb_peers WHERE node <> 0;").ExecuteReader())
            {
                while (reader.Read())
                {
                    applied[new ReplicaId(unchecked((ulong)reader.GetInt64(0)))] = new Hlc((ulong)reader.GetInt64(1));
                }
            }

            return (CollectedBelow(), applied);
        }, cancellationToken);

    /// <summary>The highest stamp this file applied from any epoch of <paramref name="origin"/>'s machine (0 when none).</summary>
    internal ValueTask<Hlc> GetAppliedFromAsync(ReplicaId origin, CancellationToken cancellationToken) =>
        RunAsync(() => new Hlc(AppliedByMachine().GetValueOrDefault(Machine(origin))), cancellationToken);

    /// <summary><paramref name="peer"/>'s highest reported stable stamp here (0 when it never reported).</summary>
    internal ValueTask<Hlc> GetReportedStableAsync(ReplicaId peer, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command("SELECT stable FROM _tb_peers WHERE node = $node;");
            command.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            return command.ExecuteScalar() is long stable ? new Hlc((ulong)stable) : default;
        }, cancellationToken);

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

    // Worker-only.
    private (Hlc Stable, Hlc Horizon) Bounds(Hlc now)
    {
        var stable = now.Value;
        var reported = ulong.MaxValue;
        using (var reader = Command("SELECT node, applied, stable, seen FROM _tb_peers WHERE node <> 0;").ExecuteReader())
        {
            while (reader.Read())
            {
                if (!Member.Gone(new ReplicaId(unchecked((ulong)reader.GetInt64(0))), reader.GetInt64(3), now.Milliseconds))
                {
                    stable = Math.Min(stable, (ulong)reader.GetInt64(1));
                    reported = Math.Min(reported, (ulong)reader.GetInt64(2));
                }
            }
        }

        var horizon = Math.Min(stable, reported);
        return (new Hlc(stable), new Hlc(horizon > HorizonMargin ? horizon - HorizonMargin : 0));
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

            if (dead.Count > 0)
            {
                Collected(horizon);
            }

            return dead.Count;
        }, cancellationToken, mutation: true);

    /// <summary>
    /// A copying refill (section 10): forgets each of <paramref name="keys"/> (states the refilling peer lacks) that the peer
    /// collected. That is a state wholly below <paramref name="collectedBelow"/>, the peer's collection watermark, of which
    /// the peer once applied an entry (<paramref name="applied"/>, its applied stamp per origin): the peer held the document
    /// and dropped it with a deletion, which wins. A state the peer never held stays (it was made where the peer could not
    /// see it), and so does one above the watermark (the peer has not collected it, so it lost it, and anti-entropy gives it
    /// back). Returns how many went. The changes feed does not see the drop yet: it waits for R8's local-only feed origin
    /// (a drop filed under this machine's own node could be skipped silently by a reader resuming elsewhere, #120).
    /// </summary>
    internal ValueTask<int> ForgetAsync(
        IReadOnlyList<(string Collection, string Id)> keys, Hlc collectedBelow, IReadOnlyDictionary<ReplicaId, Hlc> applied, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var held = new Dictionary<ulong, ulong>();
            foreach (var (node, stamp) in applied)
            {
                held[Machine(node)] = Math.Max(held.GetValueOrDefault(Machine(node)), stamp.Value);
            }

            var forgotten = 0;
            foreach (var (collection, id) in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = ReadState(collection, id);
                if (state.MaxStamp.Value == 0 || state.MaxStamp.Value >= collectedBelow.Value
                    || !Versions(state).Any(version => held.TryGetValue(Machine(version.Node), out var stamp) && version.Stamp.Value <= stamp))
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

            if (forgotten > 0)
            {
                Collected(collectedBelow);
            }

            return forgotten;
        }, cancellationToken, mutation: true);

    /// <summary>
    /// After a refill copied a peer's states (section 10, step 4): this file holds every event the peer applied, so each
    /// origin's applied stamp here rises to the peer's, and the peer's collections become this file's (its watermark rises
    /// to <paramref name="collectedBelow"/>). Uncaptured bookkeeping; origins this file has no row for are left alone.
    /// </summary>
    internal async ValueTask AdoptAsync(Hlc collectedBelow, IReadOnlyDictionary<ReplicaId, Hlc> applied, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        _ = await RunAsync(() =>
        {
            foreach (var (node, stamp) in applied.Where(entry => entry.Key.Value != 0))
            {
                var update = Command("UPDATE _tb_peers SET applied = max(applied, $applied) WHERE node = $node;");
                update.Parameters.AddWithValue("$node", unchecked((long)node.Value));
                update.Parameters.AddWithValue("$applied", Version(stamp));
                update.ExecuteNonQuery();
            }

            _appliedByMachine = null;
            Collected(collectedBelow);
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask<bool> JoinReplicatedAsync(string collection, string id, DocumentState state, EntryVersion at, bool whole, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return await RunAsync(() => JoinReplicated(collection, id, state, at, whole, cancellationToken), cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only, inside a mutation: see the remarks on this part of the class.
    private bool JoinReplicated(string collection, string id, DocumentState state, EntryVersion at, bool whole, CancellationToken cancellationToken)
    {
        var below = CollectedBelow().Value;
        if (below == 0 || (whole ? Versions(state).All(version => version.Stamp.Value >= below) : at.Stamp.Value >= below) || HasState(collection, id))
        {
            return Join(collection, id, state, at, cancellationToken);
        }

        if (!whole)
        {
            // Applied here before (a replay of what this file joined, then collected), or never: only its whole state can tell
            // whether it loses to a collected deletion.
            return Held(at) ? false : throw new StaleDeltaException("a delta older than a collected deletion needs its document's whole state");
        }

        var held = Versions(state).Where(Held).ToList();
        if (held.Count == 0 || held.Any(version => version.Stamp.Value >= below))
        {
            return Join(collection, id, state, at, cancellationToken);
        }

        var since = Since(state, new Hlc(below));
        return since.MaxStamp.Value != 0 && Join(collection, id, since, at, cancellationToken);
    }

    // Worker-only: whether this file once held the event: minted here (any epoch), or at or below what it applied from that
    // event's machine. A refill raises the applied stamps to the peer's, whose states it copied.
    private bool Held(EntryVersion version) =>
        (version.Node.Region == Member.Id.Region && version.Node.Member == Member.Id.Member)
        || (AppliedByMachine().TryGetValue(Machine(version.Node), out var applied) && version.Stamp.Value <= applied);

    // Worker-only.
    private bool HasState(string collection, string id)
    {
        var held = Command("SELECT 1 FROM _tb_state WHERE collection = $collection AND id = $id;", collection);
        held.Parameters.AddWithValue("$id", id);
        return held.ExecuteScalar() is not null;
    }

    // Worker-only.
    private Hlc CollectedBelow() =>
        _collectedBelow ??= Command(ReadCollected).ExecuteScalar() is long collected ? new Hlc((ulong)collected) : default;

    // Worker-only, inside a mutation: raises the collection watermark to `horizon`.
    private void Collected(Hlc horizon)
    {
        if (horizon.Value <= CollectedBelow().Value)
        {
            return;
        }

        var raise = Command(RaiseCollected);
        raise.Parameters.AddWithValue("$applied", Version(horizon));
        if (raise.ExecuteNonQuery() == 0)
        {
            var insert = Command(InsertCollected);
            insert.Parameters.AddWithValue("$applied", Version(horizon));
            insert.ExecuteNonQuery();
        }

        _collectedBelow = horizon;
    }

    // Worker-only: the highest applied stamp per machine (every epoch of one data directory is one machine).
    private Dictionary<ulong, ulong> AppliedByMachine()
    {
        if (_appliedByMachine is { } known)
        {
            return known;
        }

        var applied = new Dictionary<ulong, ulong>();
        using (var reader = Command("SELECT node, applied FROM _tb_peers WHERE node <> 0;").ExecuteReader())
        {
            while (reader.Read())
            {
                var machine = Machine(new ReplicaId(unchecked((ulong)reader.GetInt64(0))));
                applied[machine] = Math.Max(applied.GetValueOrDefault(machine), (ulong)reader.GetInt64(1));
            }
        }

        return _appliedByMachine = applied;
    }

    // A replica id without its epoch: the data directory that minted it.
    private static ulong Machine(ReplicaId node) => node.Value >> 16;

    // The entries of `state` at or above `below`, normalized.
    private static DocumentState Since(DocumentState state, Hlc below)
    {
        var fields = ImmutableSortedDictionary.CreateBuilder<string, FieldState>(Utf8Ordinal.Instance);
        foreach (var (name, field) in state.Fields)
        {
            BaseEntry? @base = field.Base is { } kept && kept.Version.Stamp.Value >= below.Value ? kept : null;
            var counters = field.Counters.RemoveRange(field.Counters.Where(entry => entry.Value.Epoch.Stamp.Value < below.Value).Select(entry => entry.Key));
            var dots = field.Dots.RemoveRange(field.Dots.Where(entry => entry.Key.Version.Stamp.Value < below.Value).Select(entry => entry.Key));
            if (@base is not null || !counters.IsEmpty || !dots.IsEmpty)
            {
                fields[name] = new FieldState(@base, counters, dots);
            }
        }

        ClearEntry? clear = state.Clear is { } entry && entry.Version.Stamp.Value >= below.Value ? entry : null;
        return DocumentState.Bottom.Join(new DocumentState(clear, fields.ToImmutable()));
    }

    // Every version a state carries: its clear register, field bases, counter epochs, and dots with their removals.
    private static IEnumerable<EntryVersion> Versions(DocumentState state)
    {
        if (state.Clear is { } clear)
        {
            yield return clear.Version;
        }

        foreach (var field in state.Fields.Values)
        {
            if (field.Base is { } @base)
            {
                yield return @base.Version;
            }

            foreach (var counter in field.Counters.Values)
            {
                yield return counter.Epoch;
            }

            foreach (var (dot, entry) in field.Dots)
            {
                yield return dot.Version;
                if (entry.RemovedAt is { } removed)
                {
                    yield return removed;
                }
            }
        }
    }
}
