using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The CDC tailer's reads and bookkeeping (docs/terrabase/multi-region-replication.md section 7): change groups rebuilt
/// from <c>turso_cdc</c> on demand, each peer's acknowledged change id in <c>_tb_peers.acked</c>, and CDC retention.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    // CDC rows read per query; a transaction may span pages.
    private const int TailPageRows = 4096;

    /// <summary>
    /// Deletes the CDC rows of a client database that are shipped (docs/terrabase/multi-region-replication.md section 7):
    /// below the newest commit row that every peer heard from since <c>$gone</c> (Unix ms; 24 h ago, see
    /// <see cref="LocalReplica.GoneBefore"/>) acknowledged, continuous backup read (at or
    /// below <c>$below</c>), and, once the database has peers, that committed at least 60 s before <c>$now</c> (Unix
    /// seconds). The 60 s hold covers a peer whose stream has not reached the database yet, so it has no <c>_tb_peers</c>
    /// row; a database without peers keeps nothing back, and a peer that holds no row for longer refills (section 10). Deleting below a commit row keeps whole transactions and the newest row, since an empty
    /// <c>turso_cdc</c> restarts its ids at 1 after a reopen.
    /// <para>
    /// A peer's ack holds a row for at most 24 h after the row committed (its persisted <c>change_time</c>): <c>seen</c> is
    /// refreshed by anything heard from a peer (its joined writes, stable stamps, an ack that trickles forward), so it does
    /// not bound how long an ack holds rows. Past that age every row up to the last whole transaction before the first
    /// younger row may go whatever the acks, never above <c>$below</c> (backup); the commit row deleted below stays, so
    /// the newest row does too. A peer whose ack this passes refills (<see cref="RetainsAsync"/>). The rows looked at are
    /// the aged prefix (about what is deleted) and one transaction's rows back to its commit; while the row after the
    /// oldest is younger than 24 h, an ack only reads that row. An evicted peer (<c>seen</c> 0, see <see cref="TrimAsync"/>)
    /// holds nothing, during the 24 h after a start too.
    /// </para>
    /// </summary>
    internal const string Retention = $"""
        DELETE FROM turso_cdc WHERE change_id < max(coalesce((
            SELECT change_id FROM turso_cdc
            WHERE change_id <= min($below, coalesce((SELECT min({ScopedAcked}) FROM _tb_peers WHERE seen > 0 AND seen >= $gone), $below))
                AND change_type = 2 AND (change_time <= $now - 60 OR NOT EXISTS (SELECT 1 FROM _tb_peers))
            ORDER BY change_id DESC LIMIT 1), 0), coalesce(CASE WHEN (SELECT change_time FROM turso_cdc ORDER BY change_id LIMIT 1 OFFSET 1) <= $now - 86400 THEN (
            SELECT change_id FROM turso_cdc
            WHERE change_id <= $below AND change_type = 2 AND change_id < coalesce((
                SELECT change_id FROM turso_cdc WHERE change_id > $below OR change_time > $now - 86400 ORDER BY change_id LIMIT 1),
                (SELECT max(change_id) + 1 FROM turso_cdc))
            ORDER BY change_id DESC LIMIT 1) END, 0));
        """;

    /// <summary>
    /// A <c>_tb_peers</c> row's ack as retention may count it: an ack of another incarnation than the file's names rows of
    /// another file's change log, so it counts as 0 (the peer still holds every row back) rather than releasing any.
    /// </summary>
    private const string ScopedAcked = "CASE WHEN incarnation = (SELECT incarnation FROM commit_offset WHERE id = 1) THEN acked ELSE 0 END";

    private const string TailPage = """
        SELECT change_id, change_type, table_name, after, change_time FROM turso_cdc
        WHERE change_id > $after ORDER BY change_id LIMIT $limit;
        """;

    /// <summary>
    /// The groups of this machine's own writes committed after change id <paramref name="after"/> (see
    /// <see cref="IChangeGroupSource.ReadAsync"/>), and the highest commit's change id up to which none was found past
    /// <paramref name="after"/>: a later read from anywhere in between may start there; and when the first group committed
    /// (Unix seconds).
    /// </summary>
    internal ValueTask<(List<OriginGroup> Groups, long Quiet, long? FirstCommitted)> ReadChangeGroupsAsync(
        long after, int maxGroups, int maxBytes, ReplicaId self, CancellationToken cancellationToken) =>
        RunAsync(() => ReadChangeGroups(after, maxGroups, maxBytes, self, cancellationToken), cancellationToken);

    // A supported immutable version can travel as a dependency without rewriting its future remote stamp.
    private DocumentState? ReadCatalogVersionProof()
    {
        var state = ReadState(ManagementCatalog.Collection, ManagementCatalog.VersionId);
        if (state.Materialize() is not { } body)
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateObject().Count() == 1
            && document.RootElement.TryGetProperty("\0", out var version) && version.ValueKind == JsonValueKind.String
            && version.GetString() == ManagementCatalog.Version ? state : null;
    }

    // Worker-only. Rows come in commit order: one writer per file, so a transaction's rows are contiguous and end with its
    // commit row (change_type 2), and a reader never sees part of one.
    private (List<OriginGroup> Groups, long Quiet, long? FirstCommitted) ReadChangeGroups(long after, int maxGroups, int maxBytes, ReplicaId self, CancellationToken cancellationToken)
    {
        try
        {
            return DecodeChangeGroups(after, maxGroups, maxBytes, self, cancellationToken);
        }
        catch (InvalidDataException error)
        {
            // Tail gauges and retention catch decoder errors: fault this local copy before either can swallow one.
            NoteCopyFault("stored replication data could not be decoded");
            if (StoredDataCorruption.Is(error))
            {
                throw;
            }

            throw StoredDataCorruption.Create("stored replication data could not be decoded", error);
        }
    }

    private (List<OriginGroup> Groups, long Quiet, long? FirstCommitted) DecodeChangeGroups(long after, int maxGroups, int maxBytes, ReplicaId self, CancellationToken cancellationToken)
    {
        List<OriginGroup> groups = [];
        var quiet = after;
        // The writer creates turso_cdc when it first opens: a file this machine never wrote has captured nothing.
        if (Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null)
        {
            return (groups, quiet, null);
        }

        var bytes = 0;
        long? first = null;
        var transaction = new TailTransaction(store.IsSystem);
        for (var from = after; ;)
        {
            var command = Command(TailPage);
            command.Parameters.AddWithValue("$after", from);
            command.Parameters.AddWithValue("$limit", (long)TailPageRows);
            var rows = 0;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows++;
                    from = reader.GetInt64(0);
                    var type = reader.GetInt64(1);
                    // Turso's commit rows carry no table and no after-image, its delete rows no after-image. A row whose type
                    // was damaged into one of those values still has its own table or image: taking it at its type would drop
                    // the write it captured, so the read is refused.
                    if ((type == 2 && (!reader.IsDBNull(2) || !reader.IsDBNull(3))) || (type == -1 && !reader.IsDBNull(3)))
                    {
                        throw new InvalidDataException($"a captured change of type {type} has the shape of another type");
                    }

                    if (type == 2)
                    {
                        if (transaction.Deltas.Count > 0)
                        {
                            var size = transaction.Deltas.Sum(delta => DocumentStateEncoding.Encode(delta.State).Length);
                            if (groups.Count > 0 && bytes + size > maxBytes)
                            {
                                return (groups, quiet, first);
                            }

                            groups.Add(new OriginGroup(from, transaction.Deltas));
                            first ??= reader.IsDBNull(4) ? null : reader.GetInt64(4);
                            bytes += size;
                            if (groups.Count == maxGroups)
                            {
                                return (groups, quiet, first);
                            }
                        }
                        else if (groups.Count == 0)
                        {
                            quiet = from;
                        }

                        transaction = new TailTransaction(store.IsSystem);
                        continue;
                    }

                    // Turso writes 1 (insert), 0 (update), -1 (delete) and 2 (commit). Any other type is a damaged row: skipping it
                    // could leave an acknowledged write unshipped, so the read is refused instead.
                    if (type is not (-1 or 0 or 1))
                    {
                        throw new InvalidDataException($"a captured change has change type {type}, which no capture writes");
                    }

                    // A delete carries no after-image: a deleted document's state row says live = 0, so it needs none.
                    // The changes feed's own rows (R8) are bookkeeping of this machine, never shipped.
                    if (type is 0 or 1 && reader.GetString(2) is var table && !ChangeLog.EngineTable(table) && table is not ("_tb_feed" or "_tb_feed_floor"))
                    {
                        transaction.Add(table, reader.IsDBNull(3)
                            ? throw new InvalidDataException("a captured insert or update has no after-image")
                            : SqliteSession.Bytes(reader, 3), self);
                    }
                }
            }

            if (rows < TailPageRows)
            {
                // What is left after the last commit row is nothing: a reader sees whole transactions only.
                return (groups, quiet, first);
            }
        }
    }

    /// <summary>
    /// The incarnation of this file, read in this transaction: under the writer for a write transaction, so a file reopened
    /// at the same path while the caller waited for it (a copy, restore or rebuild) shows as the other incarnation it is.
    /// </summary>
    public ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var text = Command(SqliteStatements.Incarnation).ExecuteScalar() as string;
            if (text is not { Length: 26 } || text[0] is < '0' or > '7' || !Ulid.TryParse(text, out var incarnation)
                || incarnation == Ulid.Empty || incarnation.ToString() != text)
            {
                throw StoredDataCorruption.Create("the stored database incarnation is missing or invalid");
            }

            return incarnation;
        }, cancellationToken);

    /// <summary>
    /// See <see cref="IChangeGroupSource.AckedAsync"/>; a row of another incarnation returns a zero cursor requiring refill.
    /// Null when the peer has no ACK scope yet, or its position exceeds the current head. Copies and restores reset stored
    /// ACKs in <see cref="TursoCopy"/> while preserving their old scope until a new-incarnation ACK commits.
    /// </summary>
    internal ValueTask<SourceCursor?> GetAckedAsync(ReplicaId peer, Ulid incarnation, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command("SELECT acked, incarnation FROM _tb_peers WHERE node = $node;");
            command.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            long acked;
            string scope;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return (SourceCursor?)null;
                }

                acked = reader.GetInt64(0);
                scope = reader.GetString(1);
            }

            if (scope.Length == 0)
            {
                return null;
            }

            if (scope != incarnation.ToString())
            {
                // Keep the old scope until a refill ACK commits: reconnects must not lose this requirement.
                return new SourceCursor(incarnation, 0) { RequiresRefill = true };
            }

            var newest = Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null ? 0
                : Command("SELECT coalesce(max(change_id), 0) FROM turso_cdc;").ExecuteScalar() is long id ? id : 0;
            return acked <= newest ? new SourceCursor(incarnation, acked) : (SourceCursor?)null;
        }, cancellationToken);

    /// <summary>
    /// Whether every row after <paramref name="after"/> is still here: retention deletes a prefix, so they are exactly when
    /// the oldest row left is at most the next one. A file that never captured anything holds every group (none).
    /// </summary>
    internal ValueTask<bool> RetainsAsync(long after, CancellationToken cancellationToken) =>
        RunAsync(() =>
            Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null
            || Command("SELECT min(change_id) FROM turso_cdc;").ExecuteScalar() is not long first
            || first <= after + 1, cancellationToken);

    /// <summary>The newest change id, or 0: where a refilled peer's stream goes on.</summary>
    internal ValueTask<long> GetHeadAsync(CancellationToken cancellationToken) =>
        RunAsync(() => Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null ? 0
            : Command("SELECT coalesce(max(change_id), 0) FROM turso_cdc;").ExecuteScalar() is long head ? head : 0, cancellationToken);

    /// <summary>
    /// Retention without an acknowledgement (uncaptured): first each peer's acknowledged id rises over the rows past it that
    /// hold no group of <paramref name="self"/> (joined remote writes and repairs, which are never shipped, so a peer has
    /// nothing there to acknowledge: a replica that only receives would otherwise keep every joined row), when every row
    /// past it is still here (after a cut the peer refills instead), then a peer whose
    /// unacknowledged CDC rows pass 1 GiB stops holding them (its <c>seen</c> goes to 0 until it is heard from again, and it
    /// refills when it returns), then what <see cref="Retention"/> allows is deleted. Returns the CDC rows left, and the
    /// smallest acknowledged id of a peer still holding rows, each raise as the range it proved to hold no local group, and
    /// the oldest change id kept.
    /// </summary>
    internal async ValueTask<(long Left, long? Floor, List<(long From, long To)> Quiet, long Oldest)> TrimAsync(
        long? backupAnchor, DateTimeOffset now, ReplicaId self, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            if (Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null)
            {
                return (0L, (long?)null, new List<(long From, long To)>(), 0L);
            }

            var newest = Command("SELECT coalesce(max(change_id), 0) FROM turso_cdc;").ExecuteScalar() is long head ? head : 0;
            // An ack of another incarnation counts as 0 and never rises: it names rows of another file's change log.
            List<(long Node, long Acked, bool Current)> peers = [];
            var incarnation = Command(SqliteStatements.Incarnation).ExecuteScalar() as string ?? throw new InvalidDataException("database incarnation is missing");
            var peerRows = Command("SELECT node, acked, incarnation = $incarnation FROM _tb_peers WHERE seen > 0;");
            peerRows.Parameters.AddWithValue("$incarnation", incarnation);
            using (var reader = peerRows.ExecuteReader())
            {
                while (reader.Read())
                {
                    var current = reader.GetInt64(2) != 0;
                    peers.Add((reader.GetInt64(0), current ? reader.GetInt64(1) : 0, current));
                }
            }

            // Peers at one id share one read. Only an ack whose following rows are all still here can rise: past a cut, the
            // deleted rows may have held groups the peer lacks, and it must refill instead. A damaged row past an ack leaves
            // that ack where it is and faults the copy, so this transaction cannot commit (section 7).
            List<(long From, long To)> raised = [];
            var oldest = Command("SELECT min(change_id) FROM turso_cdc;").ExecuteScalar() is long kept ? kept : newest + 1;
            foreach (var at in peers.Where(peer => peer.Current).Select(peer => peer.Acked).Where(acked => acked < newest && oldest <= acked + 1).Distinct().ToList())
            {
                long quiet;
                try
                {
                    (_, quiet, _) = ReadChangeGroups(at, 1, 1, self, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    // ReadChangeGroups marked and noted the fault, so the commit refuses everything this pass wrote.
                    continue;
                }

                if (quiet > at)
                {
                    var raise = Command("UPDATE _tb_peers SET acked = $quiet WHERE seen > 0 AND acked = $acked AND incarnation = $incarnation;");
                    raise.Parameters.AddWithValue("$quiet", quiet);
                    raise.Parameters.AddWithValue("$acked", at);
                    raise.Parameters.AddWithValue("$incarnation", incarnation);
                    raise.ExecuteNonQuery();
                    peers = [.. peers.Select(peer => peer.Current && peer.Acked == at ? (peer.Node, quiet, true) : peer)];
                    raised.Add((at, quiet));
                }
            }

            foreach (var (node, acked, _) in peers.Where(peer => newest - peer.Acked > FarBehindRows))
            {
                var behind = Command("SELECT coalesce(sum(length(after)), 0) FROM turso_cdc WHERE change_id > $acked;");
                behind.Parameters.AddWithValue("$acked", acked);
                if (behind.ExecuteScalar() is long bytes && bytes > FarBehindBytes)
                {
                    var evict = Command("UPDATE _tb_peers SET seen = 0 WHERE node = $node;");
                    evict.Parameters.AddWithValue("$node", node);
                    evict.ExecuteNonQuery();
                }
            }

            var delete = Command(Retention);
            delete.Parameters.AddWithValue("$below", backupAnchor ?? long.MaxValue);
            delete.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            delete.Parameters.AddWithValue("$gone", Member.GoneBefore(now.ToUnixTimeMilliseconds()));
            delete.ExecuteNonQuery();
            var left = Command("SELECT coalesce(max(change_id) - min(change_id) + 1, 0) FROM turso_cdc;").ExecuteScalar() is long rows ? rows : 0;
            var floor = Command($"SELECT min({ScopedAcked}) FROM _tb_peers WHERE seen > 0 AND seen >= $gone;");
            floor.Parameters.AddWithValue("$gone", Member.GoneBefore(now.ToUnixTimeMilliseconds()));
            var oldestKept = Command("SELECT coalesce(min(change_id), 0) FROM turso_cdc;").ExecuteScalar() is long first ? first : 0;
            return (left, floor.ExecuteScalar() is long min ? min : (long?)null, raised, oldestKept);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // A peer this far behind in rows has its backlog's bytes measured; past FarBehindBytes it stops holding rows.
    private const long FarBehindRows = 65_536;
    private const long FarBehindBytes = 1L << 30;

    /// <summary>The smallest change id a peer heard from since <c>now - 24 h</c> acknowledged, or null when there is none.</summary>
    internal ValueTask<long?> GetFloorAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var floor = Command($"SELECT min({ScopedAcked}) FROM _tb_peers WHERE seen > 0 AND seen >= $gone;");
            floor.Parameters.AddWithValue("$gone", Member.GoneBefore(now.ToUnixTimeMilliseconds()));
            return floor.ExecuteScalar() is long acked ? acked : (long?)null;
        }, cancellationToken);

    /// <summary>
    /// Raises <paramref name="peer"/>'s acknowledged change id of <paramref name="incarnation"/>, or sets it to 0 (a new or
    /// stale row, or one of another incarnation), so its unshipped rows stay, and marks it heard from at
    /// <paramref name="now"/>; uncaptured: the tailer's own bookkeeping. Drops the rows of the peer's older
    /// epochs. Then deletes what <see cref="Retention"/> allows, continuous backup's <paramref name="backupAnchor"/> included.
    /// Returns the CDC rows left, and the smallest change id a peer heard from in the last 24 h acknowledged (null when there is none);
    /// null, with nothing written, when the file is no longer <paramref name="incarnation"/>.
    /// </summary>
    internal async ValueTask<(long Left, long? Floor)?> RecordAckedAsync(
        ReplicaId peer, Ulid incarnation, long changeId, Hlc stable, long? backupAnchor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            // Refused, with nothing written, unless the file is still the incarnation the ack names: checked again here, under
            // the writer, so no path records an ack against a copy reopened in its place.
            if (Command(SqliteStatements.Incarnation).ExecuteScalar() as string != incarnation.ToString())
            {
                return ((long Left, long? Floor)?)null;
            }

            // 0 resets a row GetAckedAsync found stale; an acknowledgement only raises an ack of the same incarnation.
            var update = Command("""
                UPDATE _tb_peers SET acked = CASE WHEN $acked = 0 OR incarnation <> $incarnation THEN $acked ELSE max(acked, $acked) END,
                    stable = max(stable, $stable), seen = $seen, incarnation = $incarnation
                WHERE node = $node;
                """);
            update.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            update.Parameters.AddWithValue("$incarnation", incarnation.ToString());
            update.Parameters.AddWithValue("$acked", changeId);
            update.Parameters.AddWithValue("$stable", Version(stable));
            update.Parameters.AddWithValue("$seen", now.ToUnixTimeMilliseconds());
            if (update.ExecuteNonQuery() == 0)
            {
                var insert = Command("INSERT INTO _tb_peers (node, applied, acked, stable, seen, incarnation) VALUES ($node, 0, $acked, $stable, $seen, $incarnation);");
                insert.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
                insert.Parameters.AddWithValue("$incarnation", incarnation.ToString());
                insert.Parameters.AddWithValue("$acked", changeId);
                insert.Parameters.AddWithValue("$stable", Version(stable));
                insert.Parameters.AddWithValue("$seen", now.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
            }

            // The peer's older epochs (ReplicaId: it restarted uncleanly) never ack again: their rows go, so they do not hold
            // retention and the tail lag for 24 h. Same region and member, lower epoch: the ids just below the peer's.
            var retire = Command("DELETE FROM _tb_peers WHERE node >= $machine AND node < $node;");
            retire.Parameters.AddWithValue("$machine", unchecked((long)(peer.Value & ~(ulong)ushort.MaxValue)));
            retire.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            retire.ExecuteNonQuery();

            var delete = Command(Retention);
            // Nor the rows the shape frontier has not walked yet, for a bounded time: this ack's proof comes after it (FR-25).
            var hold = !store.RequiresRemoteProof || store.IsSystem ? null : ShapeFrontierHold(session.Connection, transaction, now.ToUnixTimeSeconds());
            delete.Parameters.AddWithValue("$below", Math.Min(backupAnchor ?? long.MaxValue, hold ?? long.MaxValue));
            delete.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            delete.Parameters.AddWithValue("$gone", Member.GoneBefore(now.ToUnixTimeMilliseconds()));
            delete.ExecuteNonQuery();
            // change_id is turso_cdc's rowid alias: both ends are seeks, and deleted ids never come back between them.
            var left = Command("SELECT coalesce(max(change_id) - min(change_id) + 1, 0) FROM turso_cdc;").ExecuteScalar() is long rows ? rows : 0;
            var floor = Command($"SELECT min({ScopedAcked}) FROM _tb_peers WHERE seen > 0 AND seen >= $gone;");
            floor.Parameters.AddWithValue("$gone", Member.GoneBefore(now.ToUnixTimeMilliseconds()));
            return (left, floor.ExecuteScalar() is long acked ? acked : (long?)null);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // The rows of one Turso transaction, as far as the tailer needs them: its local groups' deltas, and the after-image of
    // each document row, so that every state row pairs with the last document row before it (R2a: a request may rewrite a
    // document's row several times and writes its state row once, at its end).
    private sealed class TailTransaction(bool isSystem)
    {
        // Document rows before the first local state row, undecoded: a transaction of remote joins never decodes them.
        private List<(string Table, byte[] Image)>? _rows = [];
        private Dictionary<(string Table, string Id), string>? _documents;

        public List<ChangeDelta> Deltas { get; } = [];

        public void Add(string table, byte[] image, ReplicaId self)
        {
            if (table != "_tb_state")
            {
                if (_documents is null)
                {
                    _rows!.Add((table, image));
                }
                else
                {
                    Document(table, image);
                }

                return;
            }

            // collection, id, bucket, state, stamp, origin, live, h1, h2
            if (SqliteRecord.Decode(image) is not [string collection, string id, _, string state, long stamp, long origin, long live, ..])
            {
                throw new InvalidDataException("a captured _tb_state row does not have the state table's columns");
            }

            // Only this machine's writes ship: a joined row carries its origin, a repair origin 0, so nothing echoes. Any epoch
            // of this machine: a write committed before an unclean restart is still unsent.
            var node = new ReplicaId(unchecked((ulong)origin));
            if (node.Region != self.Region || node.Member != self.Member || origin == 0)
            {
                return;
            }

            if (_documents is null)
            {
                _documents = [];
                foreach (var (rowTable, rowImage) in _rows!)
                {
                    Document(rowTable, rowImage);
                }

                _rows = null;
            }

            var document = live == 0 ? null
                : _documents.TryGetValue((collection, id), out var body) ? body
                : throw new InvalidDataException("a captured live document state has no document row before it in its transaction");
            byte[] stored;
            try
            {
                stored = Base64Url.DecodeFromChars(state);
            }
            catch (FormatException error)
            {
                throw new InvalidDataException("a captured state is not base64url", error);
            }

            var full = DocumentStateEncoding.DecodeStored(stored, document is null ? [] : Encoding.UTF8.GetBytes(document));
            var at = new Hlc(unchecked((ulong)stamp));
            if (isSystem && collection == ManagementCatalog.Collection && id == ManagementCatalog.VersionId)
            {
                // The captured dependency retains its original state, even when its stamps precede or exceed this group.
                Deltas.Insert(0, new ChangeDelta(collection, id, at, full));
            }
            else
            {
                Deltas.Add(new ChangeDelta(collection, id, at, full.DeltaAt(at)));
            }
        }

        // id, doc: a client collection table's row; collection, id, body: a row of the system database's one documents table.
        private void Document(string table, byte[] image)
        {
            switch (SqliteRecord.Decode(image))
            {
                case [string id, string doc]:
                    _documents![(table, id)] = doc;
                    break;
                case [string collection, string id, string body] when table == "documents":
                    _documents![(collection, id)] = body;
                    break;
            }
        }
    }
}
