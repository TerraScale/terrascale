using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The changes feed's index (docs/terrabase/multi-region-replication.md sections 5 and 14): one <c>_tb_feed</c> row per
/// <c>_tb_state</c> row a transaction writes, local or joined, in the same transaction, so the feed holds exactly the
/// committed states and a backup carries it with them. Each origin's rows arrive in its commit order (its own writes on
/// the single writer, a peer's on its one ordered stream), so a per-origin stamp is a cursor any machine can resume.
/// <c>_tb_feed_floor</c> lists every origin with the highest stamp retention deleted.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    // Rows read per origin and page, before completing the last stamp: a stamp is never split between pages.
    private const int FeedOriginRows = 512;

    private const string InsertFeed = "INSERT INTO _tb_feed (origin, stamp, collection, id, deleted) VALUES ($origin, $stamp, $collection, $id, $deleted);";

    private const string InsertFeedOrigin = "INSERT INTO _tb_feed_floor (origin, stamp) SELECT $origin, 0 WHERE NOT EXISTS (SELECT 1 FROM _tb_feed_floor WHERE origin = $origin);";

    private const string FeedFloors = "SELECT origin, stamp FROM _tb_feed_floor;";

    private const string FeedAfter = """
        SELECT stamp, collection, id, deleted, rowid FROM _tb_feed WHERE origin = $origin AND stamp > $after
        ORDER BY stamp, rowid LIMIT $limit;
        """;

    private const string FeedSameStamp = """
        SELECT stamp, collection, id, deleted, rowid FROM _tb_feed WHERE origin = $origin AND stamp = $stamp AND rowid > $rowid
        ORDER BY rowid LIMIT 1025;
        """;

    private const string FeedHead = "SELECT max(stamp) FROM _tb_feed WHERE origin = $origin;";

    private const string FeedDocument = "SELECT doc, octet_length(doc) FROM {0} WHERE id = $id;";

    public ValueTask<FeedPage> ReadFeedAsync(
        IReadOnlyDictionary<ReplicaId, Hlc> cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => ReadFeed(cursor, collections, maxEvents, maxBytes, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetFeedHeadAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyDictionary<ReplicaId, Hlc>>(() =>
        {
            var head = new Dictionary<ReplicaId, Hlc>();
            foreach (var (origin, _) in Floors())
            {
                var command = Command(FeedHead);
                command.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
                if (command.ExecuteScalar() is long stamp)
                {
                    head[origin] = new Hlc((ulong)stamp);
                }
            }

            return head;
        }, cancellationToken);

    public async ValueTask<int> TrimFeedAsync(Hlc before, int limit, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            var deleted = 0;
            foreach (var (origin, floor) in Floors())
            {
                var node = unchecked((long)origin.Value);
                var oldest = Command("SELECT rowid, stamp FROM _tb_feed WHERE origin = $origin AND stamp < $before ORDER BY stamp, rowid LIMIT $limit;");
                oldest.Parameters.AddWithValue("$origin", node);
                oldest.Parameters.AddWithValue("$before", Version(before));
                oldest.Parameters.AddWithValue("$limit", (long)(limit - deleted));
                var rows = new List<long>();
                var highest = (long)floor.Value;
                using (var reader = oldest.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(reader.GetInt64(0));
                        highest = Math.Max(highest, reader.GetInt64(1));
                    }
                }

                foreach (var row in rows)
                {
                    var delete = Command("DELETE FROM _tb_feed WHERE rowid = $rowid;");
                    delete.Parameters.AddWithValue("$rowid", row);
                    delete.ExecuteNonQuery();
                }

                if (rows.Count > 0)
                {
                    var raise = Command("UPDATE _tb_feed_floor SET stamp = $stamp WHERE origin = $origin;");
                    raise.Parameters.AddWithValue("$origin", node);
                    raise.Parameters.AddWithValue("$stamp", highest);
                    raise.ExecuteNonQuery();
                }

                deleted += rows.Count;
                if (deleted == limit)
                {
                    break;
                }
            }

            return deleted;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only, after a state row's write: its feed row, and its origin's floor row on the origin's first event here.
    // Metadata documents stay out: the feed carries client documents only.
    private void AppendFeed(string collection, string id, EntryVersion at, bool live)
    {
        // The feed is a client database's (GET …/changes); the catalog is a lattice database too, but keeps none.
        if (store.IsSystem || IsMetadata(collection))
        {
            return;
        }

        var origin = unchecked((long)at.Node.Value);
        var known = Command(InsertFeedOrigin);
        known.Parameters.AddWithValue("$origin", origin);
        known.ExecuteNonQuery();
        var insert = Command(InsertFeed, collection);
        insert.Parameters.AddWithValue("$origin", origin);
        insert.Parameters.AddWithValue("$stamp", Version(at.Stamp));
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$deleted", live ? 0 : 1);
        insert.ExecuteNonQuery();
    }

    // Worker-only.
    private List<(ReplicaId Origin, Hlc Floor)> Floors()
    {
        var floors = new List<(ReplicaId, Hlc)>();
        using var reader = Command(FeedFloors).ExecuteReader();
        while (reader.Read())
        {
            floors.Add((new ReplicaId(unchecked((ulong)reader.GetInt64(0))), new Hlc((ulong)reader.GetInt64(1))));
        }

        return floors;
    }

    // Worker-only. Reads each origin's next rows, merges them by (stamp, origin) up to the lowest last stamp of an origin
    // that has more, and stops between two stamps once a bound is reached; per origin, what it returns is always a prefix.
    private FeedPage ReadFeed(IReadOnlyDictionary<ReplicaId, Hlc> cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken)
    {
        var scanned = new Dictionary<ReplicaId, Hlc>(cursor);
        var rows = new List<(ReplicaId Origin, long Stamp, string Collection, string Id, bool Deleted)>();
        var more = false;
        var cap = long.MaxValue;
        foreach (var (origin, floor) in Floors())
        {
            var known = cursor.TryGetValue(origin, out var after);
            if (known && after.Value < floor.Value)
            {
                return new FeedPage([], cursor, More: false, Expired: true);
            }

            var read = OriginRows(unchecked((long)origin.Value), after, cancellationToken);
            if (read.Count >= FeedOriginRows)
            {
                // The origin has more: other origins' later rows wait for the next page, so the merge stays in order.
                more = true;
                cap = Math.Min(cap, read[^1].Stamp);
            }

            rows.AddRange(read.Select(row => (origin, row.Stamp, row.Collection, row.Id, row.Deleted)));
        }

        rows.Sort((a, b) => a.Stamp != b.Stamp ? a.Stamp.CompareTo(b.Stamp) : a.Origin.Value.CompareTo(b.Origin.Value));
        var events = new List<FeedEvent>();
        var tables = new Dictionary<string, bool>(StringComparer.Ordinal);
        long bytes = 0;
        (ReplicaId Origin, long Stamp)? last = null;
        foreach (var (origin, value, collection, id, deleted) in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value > cap)
            {
                break;
            }

            if (last != (origin, value) && (events.Count >= maxEvents || bytes >= maxBytes))
            {
                more = true;
                break;
            }

            last = (origin, value);
            var stamp = new Hlc((ulong)value);
            scanned[origin] = stamp;
            if (collections is not null && !collections.Contains(collection))
            {
                continue;
            }

            var document = deleted ? null : FeedDocumentJson(collection, id, tables);
            bytes += document?.Length ?? 0;
            // Typed as the nullable memory itself: a null byte[] would convert to empty memory, not to an absent document.
            events.Add(new FeedEvent(origin, stamp, collection, id, deleted, document is null ? null : (ReadOnlyMemory<byte>?)document));
        }

        return new FeedPage(events, scanned, more, Expired: false);
    }

    // Worker-only: an origin's rows after `after`, and every row of the last stamp read.
    private List<(long Stamp, string Collection, string Id, bool Deleted)> OriginRows(long origin, Hlc after, CancellationToken cancellationToken)
    {
        var rows = new List<(long, string, string, bool)>();
        var command = Command(FeedAfter);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$after", Version(after));
        command.Parameters.AddWithValue("$limit", (long)FeedOriginRows);
        long rowid = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
                rowid = reader.GetInt64(4);
            }
        }

        if (rows.Count < FeedOriginRows)
        {
            return rows;
        }

        // A transaction writes at most 1024 documents, so one stamp holds at most 1024 rows.
        var same = Command(FeedSameStamp);
        same.Parameters.AddWithValue("$origin", origin);
        same.Parameters.AddWithValue("$stamp", rows[^1].Item1);
        same.Parameters.AddWithValue("$rowid", rowid);
        using (var reader = same.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
            }
        }

        return rows;
    }

    // Worker-only: the document's current JSON, or null when its collection or row is gone (deleted since, dropped, or a
    // hidden uniqueness loser).
    private byte[]? FeedDocumentJson(string collection, string id, Dictionary<string, bool> tables)
    {
        if (!tables.TryGetValue(collection, out var exists))
        {
            tables[collection] = exists = HasTable(collection);
        }

        if (!exists)
        {
            return null;
        }

        using var reader = TableCommand(FeedDocument, collection, id).ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ReserveJson(reader.GetInt64(1));
        return SqliteSession.Bytes(reader, 0);
    }
}
