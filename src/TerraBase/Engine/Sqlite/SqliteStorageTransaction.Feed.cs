using TerraBase.Engine.Lattices;
using ZLogger;

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
        FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken) =>
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

            // An origin whose events all aged out leaves the feed and the cursors; see FeedCursor.Forget.
            var forget = Command("DELETE FROM _tb_feed_floor WHERE stamp > 0 AND stamp < $forget AND NOT EXISTS (SELECT 1 FROM _tb_feed f WHERE f.origin = _tb_feed_floor.origin);");
            forget.Parameters.AddWithValue("$forget", Version(Before(before, FeedCursor.Retention)));
            forget.ExecuteNonQuery();
            return deleted;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only, after a state row's write: its feed row, and its origin's floor row on the origin's first event here.
    // Metadata documents stay out: the feed carries client documents only.
    private void AppendFeed(string collection, string id, EntryVersion at, bool live, DocumentState state, DocumentState? before)
    {
        if (IsMetadata(collection))
        {
            return;
        }

        if (at.Node.Value != 0)
        {
            InsertFeedRow(collection, id, at, live);
            return;
        }

        // An anti-entropy repair (origin 0, R6) is filed under the original identity of every event it brought, as the
        // machines that got those events on their origins' streams filed them, so a cursor means the same everywhere. A
        // repair that changed no versioned entry (nothing to name) is this machine's event at a fresh stamp.
        var events = RepairedEvents(before ?? DocumentState.Bottom, state);
        if (events.Count == 0)
        {
            InsertFeedRow(collection, id, new EntryVersion(Member.Clock.Tick(), Member.Id), live);
            return;
        }

        foreach (var (node, stamp) in events)
        {
            HoleCheck(node, stamp);
            InsertFeedRow(collection, id, new EntryVersion(stamp, node), live);
        }
    }

    // Worker-only. A repaired event at or below the newest event of its origin already here is a hole a reader on this
    // machine may have passed: its origin's floor rises just past that newest event, so such a cursor is 410 here (and so is
    // one before the hole, which could have read it: a broken guarantee costs re-reads, never events). Holes cannot
    // happen while a refill's repairs commit before the origin's later stream batches (R3's ordering guarantee); this only
    // keeps a broken guarantee from turning into silent loss on this machine.
    private void HoleCheck(ReplicaId node, Hlc stamp)
    {
        var newest = Command("SELECT max(stamp) FROM _tb_feed WHERE origin = $origin;");
        newest.Parameters.AddWithValue("$origin", unchecked((long)node.Value));
        if (newest.ExecuteScalar() is long head && head >= Version(stamp))
        {
            var raise = Command("UPDATE _tb_feed_floor SET stamp = max(stamp, $stamp) WHERE origin = $origin;");
            raise.Parameters.AddWithValue("$origin", unchecked((long)node.Value));
            raise.Parameters.AddWithValue("$stamp", head + 1);
            raise.ExecuteNonQuery();
            store.FeedHoles?.Add(1);
            store.Logger?.ZLogError($"Changes feed hole in database {store.Name:@Database}: a repaired event of origin {node.Value:x16} at stamp {stamp.Value} landed at or below that origin's newest event {head}; the refill ordering guarantee is broken, and cursors past it are now 410 here but not on other machines");
        }
    }

    // Worker-only.
    private void InsertFeedRow(string collection, string id, EntryVersion at, bool live)
    {
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

    // The events a repair brought into a document: per node, the newest stamp among the entries that differ between the
    // two states (a clear, a field's base, a counter, an added or removed set element).
    internal static Dictionary<ReplicaId, Hlc> RepairedEvents(DocumentState before, DocumentState after)
    {
        var events = new Dictionary<ReplicaId, Hlc>();
        void Add(ReplicaId node, Hlc stamp)
        {
            if (node.Value != 0 && stamp.Value != 0 && (!events.TryGetValue(node, out var known) || known.Value < stamp.Value))
            {
                events[node] = stamp;
            }
        }

        if (after.Clear is { } clear && after.Clear != before.Clear)
        {
            Add(clear.Version.Node, clear.Version.Stamp);
        }

        foreach (var (name, field) in after.Fields)
        {
            var old = before.Fields.GetValueOrDefault(name);
            if (field.Base is { } @base && @base != old?.Base)
            {
                Add(@base.Version.Node, @base.Version.Stamp);
            }

            foreach (var (node, counter) in field.Counters)
            {
                if (old is null || !old.Counters.TryGetValue(node, out var had) || had != counter)
                {
                    Add(node, new Hlc(Math.Max(counter.Epoch.Stamp.Value, counter.Changed.Value)));
                }
            }

            foreach (var (key, dot) in field.Dots)
            {
                if (old is null || !old.Dots.TryGetValue(key, out var had))
                {
                    Add(key.Version.Node, key.Version.Stamp);
                }
                else if (had != dot && dot.RemovedAt is { } removed)
                {
                    Add(removed.Node, removed.Stamp);
                }
            }
        }

        return events;
    }

    // The stamp `age` before `stamp`'s instant (0 when that is before the epoch).
    private static Hlc Before(Hlc stamp, TimeSpan age) => new((ulong)Math.Max(0, stamp.Milliseconds - (long)age.TotalMilliseconds) << 16);

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
    private FeedPage ReadFeed(FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken)
    {
        var stamps = cursor?.Stamps ?? new Dictionary<ReplicaId, Hlc>();
        var floors = Floors();
        var scanned = new Dictionary<ReplicaId, Hlc>(stamps);
        if (cursor is not null)
        {
            // A named origin this machine forgot (all its events aged out long ago) leaves the cursor.
            var forgotten = Before(cursor.Issued, FeedCursor.Forget);
            foreach (var origin in stamps.Keys.Where(origin => stamps[origin].Value < forgotten.Value && !floors.Exists(floor => floor.Origin == origin)))
            {
                _ = scanned.Remove(origin);
            }
        }

        var rows = new List<(ReplicaId Origin, long Stamp, string Collection, string Id, bool Deleted)>();
        var more = false;
        var cap = long.MaxValue;
        foreach (var (origin, floor) in floors)
        {
            var known = stamps.TryGetValue(origin, out var after);
            // Retention deleted an event the cursor promised: above a named origin's stamp, or of an origin the issuing
            // machine had no event of, unless that event had already expired when the cursor was issued.
            if (cursor is not null && (known ? after.Value < floor.Value : floor.Value > 0 && floor.Value >= Before(cursor.Issued, FeedCursor.Retention).Value))
            {
                return new FeedPage([], stamps, More: false, Expired: true);
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
