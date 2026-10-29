using System.Text;
using TerraBase.Engine.Lattices;
using System.Text.Json;
using TerraBase.Features.Documents;
using TerraBase.Features.Qos;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{

    private const int FeedOriginRows = 512;

    private const string InsertFeed = "INSERT INTO _tb_feed (seq, commit_offset, origin, stamp, collection, id, deleted, prefix, access_before, access_after, collection_generation) VALUES ($seq, $commit, $origin, $stamp, $collection, $id, $deleted, $prefix, $access_before, $access_after, $generation);";

    private const string InsertFeedOrigin = "INSERT INTO _tb_feed_floor (origin, stamp, prefix) VALUES ($origin, 0, zeroblob(32));";

    private const string HasFeedOrigin = "SELECT 1 FROM _tb_feed_floor WHERE origin = $origin;";

    private const string FeedSequence = "SELECT seq FROM _tb_feed_sequence WHERE id = 1;";

    private const string SetFeedSequence = "UPDATE _tb_feed_sequence SET seq = $seq WHERE id = 1;";

    private const string FeedFloors = "SELECT origin, stamp FROM _tb_feed_floor;";

    private const string FeedAfter = """
        SELECT stamp, collection, id, deleted, rowid, prefix FROM _tb_feed WHERE origin = $origin AND stamp > $after
        ORDER BY stamp, rowid LIMIT $limit;
        """;

    private const string FeedSameStamp = """
        SELECT stamp, collection, id, deleted, rowid, prefix FROM _tb_feed WHERE origin = $origin AND stamp = $stamp AND rowid > $rowid
        ORDER BY rowid LIMIT 1025;
        """;

    private const string FeedHead = "SELECT max(stamp) FROM _tb_feed WHERE origin = $origin;";

    private const string FeedDocument = "SELECT doc, octet_length(doc) FROM {0} WHERE id = $id;";

    private bool _feedChanged;

    public ValueTask<FeedPage> ReadFeedAsync(
        FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => ReadFeed(cursor, collections, maxEvents, maxBytes, includeDocuments: true, cancellationToken), cancellationToken);

    public ValueTask<FeedPage> ReadFeedMetadataAsync(
        FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => ReadFeed(cursor, collections, maxEvents, maxBytes, includeDocuments: false, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetFeedHeadAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyDictionary<ReplicaId, Hlc>>(() =>
        {
            var head = new Dictionary<ReplicaId, Hlc>();
            foreach (var (origin, _) in Floors())
            {
                var command = Command(FeedHead);
                command.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
                CountRead("feed");
                if (command.ExecuteScalar() is long stamp)
                {
                    head[origin] = new Hlc((ulong)stamp);
                }
            }

            return head;
        }, cancellationToken);

    public async ValueTask<int> TrimFeedAsync(Hlc before, int limit, CancellationToken cancellationToken)
    {

        if (store.Rewriting)
        {
            return 0;
        }

        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            var deleted = 0;
            foreach (var (origin, floor) in Floors())
            {
                var node = unchecked((long)origin.Value);
                var oldest = Command("SELECT rowid, stamp, prefix FROM _tb_feed WHERE origin = $origin AND stamp < $before ORDER BY stamp, rowid LIMIT $limit;");
                oldest.Parameters.AddWithValue("$origin", node);
                oldest.Parameters.AddWithValue("$before", Version(before));
                oldest.Parameters.AddWithValue("$limit", (long)(limit - deleted));
                var rows = new List<long>();
                var highest = (long)floor.Value;
                byte[]? prefix = null;
                CountRead("feed");
                using (var reader = oldest.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(reader.GetInt64(0));
                        highest = Math.Max(highest, reader.GetInt64(1));
                        prefix = TursoSession.Bytes(reader, 2);
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
                    var raise = Command("UPDATE _tb_feed_floor SET stamp = $stamp, prefix = $prefix WHERE origin = $origin;");
                    raise.Parameters.AddWithValue("$origin", node);
                    raise.Parameters.AddWithValue("$stamp", highest);
                    raise.Parameters.AddWithValue("$prefix", prefix!);
                    raise.ExecuteNonQuery();
                }

                deleted += rows.Count;
                if (deleted == limit)
                {
                    break;
                }
            }

            var forget = Command("DELETE FROM _tb_feed_floor WHERE stamp > 0 AND stamp < $forget AND NOT EXISTS (SELECT 1 FROM _tb_feed f WHERE f.origin = _tb_feed_floor.origin);");
            forget.Parameters.AddWithValue("$forget", Version(Before(before, FeedCursor.Retention)));
            var forgotten = forget.ExecuteNonQuery();
            _feedChanged |= deleted > 0 || forgotten > 0;
            return deleted;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    private void AppendFeed(string collection, string id, EntryVersion at, bool live, DocumentOwnerProof? beforeProof, DocumentOwnerProof? afterProof, string? generation)
    {
        if (store.IsSystem || IsMetadata(collection))
        {
            return;
        }

        var origin = unchecked((long)at.Node.Value);

        var seq = Command(FeedSequence).ExecuteScalar() is long current && current < long.MaxValue
            ? current + 1
            : throw new OverflowException("the feed sequence is exhausted");
        var advance = Command(SetFeedSequence);
        advance.Parameters.AddWithValue("$seq", seq);
        advance.ExecuteNonQuery();
        var known = Command(HasFeedOrigin);
        known.Parameters.AddWithValue("$origin", origin);
        if (known.ExecuteScalar() is null)
        {
            var floor = Command(InsertFeedOrigin);
            floor.Parameters.AddWithValue("$origin", origin);
            floor.ExecuteNonQuery();
        }
        var prefix = PrefixThrough(at.Node, default, at.Stamp, CancellationToken.None).Append(at.Stamp, collection, id, !live);
        var insert = Command(InsertFeed, collection);
        insert.Parameters.AddWithValue("$seq", seq);
        insert.Parameters.AddWithValue("$commit", Offset());
        insert.Parameters.AddWithValue("$origin", origin);
        insert.Parameters.AddWithValue("$stamp", Version(at.Stamp));
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$deleted", live ? 0 : 1);
        insert.Parameters.AddWithValue("$prefix", prefix.Hash.ToArray());
        insert.Parameters.AddWithValue("$access_before", beforeProof is null ? DBNull.Value : JsonSerializer.Serialize(beforeProof, CollectionRulesJsonContext.Default.DocumentOwnerProof));
        insert.Parameters.AddWithValue("$access_after", afterProof is null ? DBNull.Value : JsonSerializer.Serialize(afterProof, CollectionRulesJsonContext.Default.DocumentOwnerProof));
        insert.Parameters.AddWithValue("$generation", generation is not null ? generation : DBNull.Value);
        insert.ExecuteNonQuery();
        RehashFeedAfter(origin, at.Stamp, prefix);
    }

    private static Hlc Before(Hlc stamp, TimeSpan age) => new((ulong)Math.Max(0, stamp.Milliseconds - (long)age.TotalMilliseconds) << 16);

    private List<(ReplicaId Origin, Hlc Floor)> Floors()
    {
        var floors = new List<(ReplicaId, Hlc)>();
        CountRead("feed");
        using var reader = Command(FeedFloors).ExecuteReader();
        while (reader.Read())
        {
            floors.Add((new ReplicaId(unchecked((ulong)reader.GetInt64(0))), new Hlc((ulong)reader.GetInt64(1))));
        }

        return floors;
    }

    private FeedPage ReadFeed(FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, bool includeDocuments, CancellationToken cancellationToken)
    {
        var stamps = cursor?.Stamps ?? new Dictionary<ReplicaId, Hlc>();
        var floors = Floors();
        var scanned = new Dictionary<ReplicaId, Hlc>(stamps);
        Dictionary<ReplicaId, FeedPrefix> prefixes = cursor?.Prefixes.ToDictionary() ?? [];
        if (cursor is not null)
        {

            var forgotten = Before(cursor.Issued, FeedCursor.Forget);
            foreach (var origin in stamps.Keys.Where(origin => stamps[origin].Value < forgotten.Value && !floors.Exists(floor => floor.Origin == origin)))
            {
                _ = scanned.Remove(origin);
                _ = prefixes.Remove(origin);
            }
        }

        foreach (var (origin, stamp) in scanned)
        {
            var floor = floors.Find(item => item.Origin == origin).Floor;
            if (!prefixes.TryGetValue(origin, out var prefix) || cursor?.Issuer.Value == 0)
            {
                prefixes[origin] = PrefixThrough(origin, floor, stamp, cancellationToken);
            }
            else if (floor.Value > stamp.Value || !PrefixThrough(origin, prefix.Floor, stamp, cancellationToken).Hash.Span.SequenceEqual(prefix.Hash.Span))
            {
                return new FeedPage([], stamps, More: false, Expired: true);
            }
        }

        var before = prefixes.ToDictionary();
        var rows = new List<(ReplicaId Origin, long Stamp, string Collection, string Id, bool Deleted, long RowId, byte[] Hash)>();
        var more = false;
        var cap = long.MaxValue;
        foreach (var (origin, floor) in floors)
        {
            var known = stamps.TryGetValue(origin, out var after);

            if (cursor is not null && (known ? after.Value < floor.Value : floor.Value > 0 && floor.Value >= Before(cursor.Issued, FeedCursor.Retention).Value))
            {
                return new FeedPage([], stamps, More: false, Expired: true);
            }

            if (!prefixes.ContainsKey(origin))
            {
                prefixes[origin] = PrefixThrough(origin, floor, floor, cancellationToken);
            }

            if (!includeDocuments)
            {
                ReserveFeedMetadata(unchecked((long)origin.Value), after, cancellationToken);
            }

            var read = OriginRows(unchecked((long)origin.Value), after, cancellationToken);
            if (read.Count >= FeedOriginRows)
            {

                more = true;
                cap = Math.Min(cap, read[^1].Stamp);
            }

            rows.AddRange(read.Select(row => (origin, row.Stamp, row.Collection, row.Id, row.Deleted, row.RowId, row.Hash)));
        }

        rows.Sort((a, b) => a.Stamp != b.Stamp ? a.Stamp.CompareTo(b.Stamp)
            : a.Origin != b.Origin ? a.Origin.Value.CompareTo(b.Origin.Value) : a.RowId.CompareTo(b.RowId));
        var events = new List<FeedEvent>();
        var tables = new Dictionary<string, bool>(StringComparer.Ordinal);
        long bytes = 0;
        (ReplicaId Origin, long Stamp)? last = null;
        foreach (var (origin, value, collection, id, deleted, rowId, hash) in rows)
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
            var prefix = new FeedPrefix(prefixes[origin].Floor, hash);
            prefixes[origin] = prefix;
            if (collections is not null && !collections.Contains(collection))
            {
                if (events.Count > 0 && (events[^1].Origin, events[^1].Stamp) == (origin, stamp))
                {
                    events[^1] = events[^1] with { Prefix = prefix };
                }

                continue;
            }

            var document = deleted || !includeDocuments ? null : FeedDocumentJson(collection, id, tables);
            bytes += includeDocuments ? document?.Length ?? 0 : (long)Encoding.UTF8.GetByteCount(id) * 6 + 128;

            var proof = Command("SELECT access_before, access_after, coalesce(octet_length(access_before),0), coalesce(octet_length(access_after),0), collection_generation FROM _tb_feed WHERE rowid = $rowid;");
            proof.Parameters.AddWithValue("$rowid", rowId);
            DocumentOwnerProof? beforeProof = null;
            DocumentOwnerProof? afterProof = null;
            string? generation = null;
            using (var reader = proof.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetInt64(2) > 32 * 1024 || reader.GetInt64(3) > 32 * 1024)
                    {
                        throw new InvalidDataException("an ownership proof exceeds its bound");
                    }

                    QosContext.ReserveResult(8 * (reader.GetInt64(2) + reader.GetInt64(3)));
                    beforeProof = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize(reader.GetString(0), CollectionRulesJsonContext.Default.DocumentOwnerProof);
                    afterProof = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize(reader.GetString(1), CollectionRulesJsonContext.Default.DocumentOwnerProof);
                    generation = reader.IsDBNull(4) ? null : reader.GetString(4);
                }
            }

            bytes += (beforeProof is null ? 0 : 512 + 6L * beforeProof.Path.Length) + (afterProof is null ? 0 : 512 + 6L * afterProof.Path.Length);
            events.Add(new FeedEvent(origin, stamp, collection, id, deleted, document is null ? null : (ReadOnlyMemory<byte>?)document)
            {
                Prefix = prefix,
                AccessBefore = beforeProof,
                AccessAfter = afterProof,
                CollectionGeneration = generation,
            });
        }

        return new FeedPage(events, scanned, more, Expired: false) { Issuer = Member.Id, Prefixes = prefixes, Before = before };
    }

    private void ReserveFeedMetadata(long origin, Hlc after, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = Command("""
            SELECT count(*), coalesce(sum(octet_length(collection) + octet_length(id)), 0)
            FROM _tb_feed WHERE origin = $origin AND stamp > $after
            AND stamp <= coalesce((SELECT stamp FROM _tb_feed WHERE origin = $origin AND stamp > $after
                ORDER BY stamp, rowid LIMIT 1 OFFSET $skip), 9223372036854775807);
            """);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$after", Version(after));
        command.Parameters.AddWithValue("$skip", (long)FeedOriginRows - 1);
        CountRead("feed");
        using var reader = command.ExecuteReader();
        reader.Read();
        var rows = reader.GetInt64(0);
        if (rows > 0)
        {
            _stateReadMemory?.Reserve(checked(reader.GetInt64(1) * 4 + rows * 512), store.Tenant);
        }
    }

    private FeedPrefix PrefixThrough(ReplicaId origin, Hlc floor, Hlc stamp, CancellationToken cancellationToken, string path = "feed")
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = Command("SELECT prefix FROM _tb_feed WHERE origin = $origin AND stamp <= $stamp ORDER BY stamp DESC, rowid DESC LIMIT 1;");
        command.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
        command.Parameters.AddWithValue("$stamp", Version(stamp));
        CountRead(path);
        if (command.ExecuteScalar() is byte[] hash)
        {
            return new FeedPrefix(floor, hash);
        }

        var baseline = Command("SELECT prefix FROM _tb_feed_floor WHERE origin = $origin;");
        baseline.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
        CountRead(path);
        return new FeedPrefix(floor, baseline.ExecuteScalar() as byte[] ?? new byte[32]);
    }

    private void RehashFeedAfter(long origin, Hlc stamp, FeedPrefix prefix)
    {
        while (true)
        {
            var rows = OriginRows(origin, stamp, CancellationToken.None);
            if (rows.Count == 0)
            {
                return;
            }

            foreach (var (value, collection, id, deleted, rowid, _) in rows)
            {
                stamp = new Hlc((ulong)value);
                prefix = prefix.Append(stamp, collection, id, deleted);
                var update = Command("UPDATE _tb_feed SET prefix = $prefix WHERE rowid = $rowid;");
                update.Parameters.AddWithValue("$prefix", prefix.Hash.ToArray());
                update.Parameters.AddWithValue("$rowid", rowid);
                update.ExecuteNonQuery();
            }
        }
    }

    private List<(long Stamp, string Collection, string Id, bool Deleted, long RowId, byte[] Hash)> OriginRows(long origin, Hlc after, CancellationToken cancellationToken)
    {
        var rows = new List<(long, string, string, bool, long, byte[])>();
        var command = Command(FeedAfter);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$after", Version(after));
        command.Parameters.AddWithValue("$limit", (long)FeedOriginRows);
        long rowid = 0;
        CountRead("feed");
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4), TursoSession.Bytes(reader, 5)));
                rowid = reader.GetInt64(4);
            }
        }

        if (rows.Count < FeedOriginRows)
        {
            return rows;
        }

        var same = Command(FeedSameStamp);
        same.Parameters.AddWithValue("$origin", origin);
        same.Parameters.AddWithValue("$stamp", rows[^1].Item1);
        same.Parameters.AddWithValue("$rowid", rowid);
        CountRead("feed");
        using (var reader = same.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4), TursoSession.Bytes(reader, 5)));
            }
        }

        return rows;
    }

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

        CountRead("feed");
        using var reader = TableCommand(FeedDocument, collection, id).ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ReserveJson(reader.GetInt64(1));
        return TursoSession.Bytes(reader, 0);
    }
}
