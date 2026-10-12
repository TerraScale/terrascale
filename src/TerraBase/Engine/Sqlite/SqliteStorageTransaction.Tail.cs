using System.Buffers.Text;
using System.Text;
using TerraBase.Engine.Lattices;
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
    /// below the newest commit row that every peer heard from in the last 24 h acknowledged, continuous backup read (at or
    /// below <c>$below</c>), and, once the database has peers, that committed at least 60 s before <c>$now</c> (Unix
    /// seconds). The 60 s hold covers a peer whose stream has not reached the database yet, so it has no <c>_tb_peers</c>
    /// row; a database without peers keeps nothing back, and a peer that holds no row for longer refills (section 10). Deleting below a commit row keeps whole transactions and the newest row, since an empty
    /// <c>turso_cdc</c> restarts its ids at 1 after a reopen.
    /// </summary>
    internal const string Retention = """
        DELETE FROM turso_cdc WHERE change_id < coalesce((
            SELECT change_id FROM turso_cdc
            WHERE change_id <= min($below, coalesce((SELECT min(acked) FROM _tb_peers WHERE seen >= $now * 1000 - 86400000), $below))
                AND change_type = 2 AND (change_time <= $now - 60 OR NOT EXISTS (SELECT 1 FROM _tb_peers))
            ORDER BY change_id DESC LIMIT 1), 0);
        """;

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

    // Worker-only. Rows come in commit order: one writer per file, so a transaction's rows are contiguous and end with its
    // commit row (change_type 2), and a reader never sees part of one.
    private (List<OriginGroup> Groups, long Quiet, long? FirstCommitted) ReadChangeGroups(long after, int maxGroups, int maxBytes, ReplicaId self, CancellationToken cancellationToken)
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
        var transaction = new TailTransaction();
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

                        transaction = new TailTransaction();
                        continue;
                    }

                    // A delete carries no after-image: a deleted document's state row says live = 0, so it needs none.
                    if (type is 0 or 1 && reader.GetString(2) is var table && !ChangeLog.EngineTable(table))
                    {
                        transaction.Add(table, SqliteSession.Bytes(reader, 3), self);
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
    /// See <see cref="IChangeGroupSource.AckedAsync"/>; null when <paramref name="peer"/> has no row yet, or when its row is
    /// above the newest change id: a restore or a copy brought it back from another file's <c>turso_cdc</c>, whose ids this
    /// file does not continue (retention always keeps the newest row, so an acknowledged id never passes it).
    /// </summary>
    internal ValueTask<long?> GetAckedAsync(ReplicaId peer, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command("SELECT acked FROM _tb_peers WHERE node = $node;");
            command.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            if (command.ExecuteScalar() is not long acked)
            {
                return null;
            }

            var newest = Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null ? 0
                : Command("SELECT coalesce(max(change_id), 0) FROM turso_cdc;").ExecuteScalar() is long id ? id : 0;
            return acked <= newest ? acked : (long?)null;
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
    /// Retention without an acknowledgement (uncaptured): first a peer whose unacknowledged CDC rows pass 1 GiB stops holding
    /// them (its <c>seen</c> goes to 0 until it is heard from again, and it refills when it returns), then what
    /// <see cref="Retention"/> allows is deleted. Returns the CDC rows left, and the smallest acknowledged id of a peer still
    /// holding rows.
    /// </summary>
    internal async ValueTask<(long Left, long? Floor)> TrimAsync(long? backupAnchor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            if (Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null)
            {
                return (0L, (long?)null);
            }

            var newest = Command("SELECT coalesce(max(change_id), 0) FROM turso_cdc;").ExecuteScalar() is long head ? head : 0;
            List<(long Node, long Acked)> peers = [];
            using (var reader = Command("SELECT node, acked FROM _tb_peers WHERE seen > 0;").ExecuteReader())
            {
                while (reader.Read())
                {
                    peers.Add((reader.GetInt64(0), reader.GetInt64(1)));
                }
            }

            foreach (var (node, acked) in peers.Where(peer => newest - peer.Acked > FarBehindRows))
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
            delete.ExecuteNonQuery();
            var left = Command("SELECT coalesce(max(change_id) - min(change_id) + 1, 0) FROM turso_cdc;").ExecuteScalar() is long rows ? rows : 0;
            var floor = Command("SELECT min(acked) FROM _tb_peers WHERE seen >= $now * 1000 - 86400000;");
            floor.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            return (left, floor.ExecuteScalar() is long min ? min : (long?)null);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // A peer this far behind in rows has its backlog's bytes measured; past FarBehindBytes it stops holding rows.
    private const long FarBehindRows = 65_536;
    private const long FarBehindBytes = 1L << 30;

    /// <summary>The smallest change id a peer heard from since <c>now - 24 h</c> acknowledged, or null when there is none.</summary>
    internal ValueTask<long?> GetFloorAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var floor = Command("SELECT min(acked) FROM _tb_peers WHERE seen >= $now * 1000 - 86400000;");
            floor.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            return floor.ExecuteScalar() is long acked ? acked : (long?)null;
        }, cancellationToken);

    /// <summary>
    /// Raises <paramref name="peer"/>'s acknowledged change id, or sets it to 0 (a new or stale row), so its unshipped rows stay, and marks
    /// it heard from at <paramref name="now"/>; uncaptured: the tailer's own bookkeeping. Then deletes what
    /// <see cref="Retention"/> allows, continuous backup's <paramref name="backupAnchor"/> included. Returns the CDC rows left,
    /// and the smallest change id a peer heard from in the last 24 h acknowledged (null when there is none).
    /// </summary>
    internal async ValueTask<(long Left, long? Floor)> RecordAckedAsync(ReplicaId peer, long changeId, long? backupAnchor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            // 0 resets a row GetAckedAsync found stale; an acknowledgement only raises it.
            var update = Command("UPDATE _tb_peers SET acked = CASE WHEN $acked = 0 THEN 0 ELSE max(acked, $acked) END, seen = $seen WHERE node = $node;");
            update.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            update.Parameters.AddWithValue("$acked", changeId);
            update.Parameters.AddWithValue("$seen", now.ToUnixTimeMilliseconds());
            if (update.ExecuteNonQuery() == 0)
            {
                var insert = Command("INSERT INTO _tb_peers (node, applied, acked, stable, seen) VALUES ($node, 0, $acked, 0, $seen);");
                insert.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
                insert.Parameters.AddWithValue("$acked", changeId);
                insert.Parameters.AddWithValue("$seen", now.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
            }

            var delete = Command(Retention);
            delete.Parameters.AddWithValue("$below", backupAnchor ?? long.MaxValue);
            delete.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            delete.ExecuteNonQuery();
            // change_id is turso_cdc's rowid alias: both ends are seeks, and deleted ids never come back between them.
            var left = Command("SELECT coalesce(max(change_id) - min(change_id) + 1, 0) FROM turso_cdc;").ExecuteScalar() is long rows ? rows : 0;
            var floor = Command("SELECT min(acked) FROM _tb_peers WHERE seen >= $now * 1000 - 86400000;");
            floor.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            return (left, floor.ExecuteScalar() is long acked ? acked : (long?)null);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // The rows of one Turso transaction, as far as the tailer needs them: its local groups' deltas, and the after-image of
    // each document row, so that every state row pairs with the last document row before it (R2a: a request may rewrite a
    // document's row several times and writes its state row once, at its end).
    private sealed class TailTransaction
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
            var full = DocumentStateEncoding.DecodeStored(Base64Url.DecodeFromChars(state), document is null ? [] : Encoding.UTF8.GetBytes(document));
            var at = new Hlc(unchecked((ulong)stamp));
            Deltas.Add(new ChangeDelta(collection, id, at, full.DeltaAt(at)));
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
