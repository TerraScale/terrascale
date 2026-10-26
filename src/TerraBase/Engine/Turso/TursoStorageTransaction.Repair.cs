using System.Buffers.Binary;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Anti-entropy reads (docs/terrabase/multi-region-replication.md section 10): the digest of each of a database's 4096
/// buckets, the XOR of its documents' <c>_tb_state.h1/h2</c>, and the whole states of chosen buckets, paged.
/// </summary>
internal sealed partial class TursoStorageTransaction
{
    /// <summary>Buckets of a database: the first 12 bits of a document's key hash.</summary>
    internal const int Buckets = 4096;

    /// <summary>4096 × (h1, h2), little-endian.</summary>
    internal ValueTask<byte[]> ReadDigestsAsync(CancellationToken cancellationToken) =>
        RunAsync(() => Digests("_tb_merkle", cancellationToken), cancellationToken);

    /// <summary>
    /// The digests as <see cref="ReadDigestsAsync"/> lays them out, recomputed from the <c>_tb_state</c> rows: what
    /// <c>_tb_merkle</c> holds unless a row was deleted, added or rehashed outside the engine. A whole-table scan.
    /// </summary>
    internal ValueTask<byte[]> ReadRowDigestsAsync(CancellationToken cancellationToken) =>
        RunAsync(() => Digests("_tb_state", cancellationToken), cancellationToken);

    /// <summary>
    /// Replaces <c>_tb_merkle</c> with the digests of the <c>_tb_state</c> rows when they differ, so the buckets of rows
    /// changed outside the engine differ from the peers' and anti-entropy repairs them. Uncaptured: the change log never saw
    /// the outside change, and a restore or a rebuild checks its own copy the same way. Whether it rewrote anything.
    /// </summary>
    internal async ValueTask<bool> ReconcileDigestsAsync(CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            var rows = Digests("_tb_state", cancellationToken);
            if (rows.AsSpan().SequenceEqual(Digests("_tb_merkle", cancellationToken)))
            {
                return false;
            }

            Command("DELETE FROM _tb_merkle;").ExecuteNonQuery();
            var insert = Command("INSERT INTO _tb_merkle (bucket, h1, h2) VALUES ($bucket, $h1, $h2);");
            var bucket = insert.Parameters.AddWithValue("$bucket", 0L);
            var h1 = insert.Parameters.AddWithValue("$h1", 0L);
            var h2 = insert.Parameters.AddWithValue("$h2", 0L);
            for (var index = 0; index < Buckets; index++)
            {
                var slot = rows.AsSpan(index * 16, 16);
                if (slot.ContainsAnyExcept((byte)0))
                {
                    bucket.Value = (long)index;
                    h1.Value = BinaryPrimitives.ReadInt64LittleEndian(slot);
                    h2.Value = BinaryPrimitives.ReadInt64LittleEndian(slot[8..]);
                    insert.ExecuteNonQuery();
                }
            }

            if (!store.IsSystem)
            {
                // Outside changes may remove bodies too: recount before repairs put them back, counting each once.
                Command("DELETE FROM _tb_collection_stats;").ExecuteNonQuery();
                foreach (var name in CollectionNames(cancellationToken))
                {
                    Command($"""
                        INSERT INTO _tb_collection_stats (collection, documents, bytes)
                        SELECT $collection, count(*), coalesce(sum(octet_length(doc)), 0) FROM {Table(name)};
                        """, name).ExecuteNonQuery();
                }
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only: the XOR per bucket of the (bucket, h1, h2) rows of `table`, _tb_merkle or _tb_state.
    private byte[] Digests(string table, CancellationToken cancellationToken)
    {
        var digests = new byte[Buckets * 16];
        // CASE keeps unchecked TEXT/BLOB values out of the driver's managed projection.
        using var reader = Command($"""
            SELECT CASE WHEN typeof(bucket) = 'integer' AND bucket >= 0 AND bucket < 4096
                AND typeof(h1) = 'integer' AND typeof(h2) = 'integer' THEN 1 ELSE 0 END,
                CASE WHEN typeof(bucket) = 'integer' THEN bucket ELSE 0 END,
                CASE WHEN typeof(h1) = 'integer' THEN h1 ELSE 0 END,
                CASE WHEN typeof(h2) = 'integer' THEN h2 ELSE 0 END
            FROM {table};
            """).ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.GetInt64(0) != 1)
            {
                throw StoredDataCorruption.Create("a stored digest has a non-integer hash or an invalid bucket");
            }

            var slot = digests.AsSpan((int)reader.GetInt64(1) * 16, 16);
            BinaryPrimitives.WriteInt64LittleEndian(slot, BinaryPrimitives.ReadInt64LittleEndian(slot) ^ reader.GetInt64(2));
            BinaryPrimitives.WriteInt64LittleEndian(slot[8..], BinaryPrimitives.ReadInt64LittleEndian(slot[8..]) ^ reader.GetInt64(3));
        }

        return digests;
    }

    /// <summary>
    /// The whole states of the documents in <paramref name="buckets"/> after <paramref name="after"/> (bucket, collection,
    /// id), in that order: at most <paramref name="maxStates"/>, and <paramref name="maxBytes"/> of encoded states unless the
    /// first alone is larger. The cursor of the next page, or null after the last.
    /// </summary>
    internal ValueTask<(List<RepairState> States, BucketCursor? Next)> ReadBucketStatesAsync(
        IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxStates, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maxStates, ChangeGroups.MaxDeltas);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
            if (buckets.Count > Buckets || buckets.Any(bucket => bucket is < 0 or >= Buckets)
                || (after is { } cursor && (cursor.Bucket is < 0 or >= Buckets
                    || !ChangeGroups.ValidKey(store.IsSystem ? ChangeGroups.System : store.Tenant!, cursor.Collection, cursor.Id))))
            {
                throw new ArgumentException("a repair read names valid buckets and a valid cursor", nameof(buckets));
            }

            List<RepairState> states = [];
            long bytes = 0;
            long pageBytes = 0;
            var ordered = buckets.Distinct().Order().ToList();
            var all = ordered.Count == Buckets && ordered[0] == 0 && ordered[^1] == Buckets - 1;
            var start = after is { } fromCursor ? ordered.FindIndex(bucket => bucket >= fromCursor.Bucket) : 0;
            for (var index = start < 0 ? ordered.Count : start; index < ordered.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bucket = ordered[index];
                var resume = after is { } from && from.Bucket == bucket ? from : null;
                // The explicit lower bound lets Turso seek without sorting the remaining suffix.
                var predicate = all
                    ? "bucket >= $bucket AND (bucket > $bucket OR (bucket = $bucket AND ($resume = 0 OR collection > $collection OR (collection = $collection AND id > $id))))"
                    : "bucket = $bucket AND ($resume = 0 OR collection > $collection OR (collection = $collection AND id > $id))";
                var order = all ? "bucket, collection, id" : "collection, id";
                var sizes = Command($"""
                    SELECT coalesce(sum(collection_bytes + id_bytes), 0), count(*),
                        coalesce(min(collection_bytes), 1), coalesce(max(collection_bytes), 0),
                        coalesce(min(id_bytes), 1), coalesce(max(id_bytes), 0),
                        count(*) - count(collection_bytes), count(*) - count(id_bytes),
                        coalesce(min(valid_hash), 1), coalesce(min(valid_bucket), 1)
                    FROM (
                        SELECT octet_length(collection) AS collection_bytes, octet_length(id) AS id_bytes,
                            typeof(h1) = 'integer' AND typeof(h2) = 'integer' AS valid_hash,
                            typeof(bucket) = 'integer' AND bucket >= 0 AND bucket < 4096 AS valid_bucket
                        FROM _tb_state WHERE {predicate}
                        ORDER BY {order} LIMIT $limit
                    );
                    """);
                Bind(sizes);
                long totalKeyBytes;
                long keyCount;
                using (var reader = sizes.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        throw StoredDataCorruption.Create("a stored repair key preflight is missing");
                    }

                    if (reader.GetInt64(8) != 1)
                    {
                        throw StoredDataCorruption.Create("a stored repair key has non-integer digest columns");
                    }

                    if (reader.GetInt64(9) != 1)
                    {
                        throw StoredDataCorruption.Create("a stored repair bucket is outside 0 to 4095 or is not an integer");
                    }

                    totalKeyBytes = reader.GetInt64(0);
                    keyCount = reader.GetInt64(1);
                    var collectionLimit = store.IsSystem ? 3L * ushort.MaxValue : 63L;
                    var idLimit = store.IsSystem ? 3L * ushort.MaxValue : 3L * 1024;
                    if (reader.GetInt64(2) <= 0 || reader.GetInt64(3) > collectionLimit
                        || reader.GetInt64(4) <= 0 || reader.GetInt64(5) > idLimit
                        || reader.GetInt64(6) != 0 || reader.GetInt64(7) != 0)
                    {
                        throw StoredDataCorruption.Create("a stored repair key exceeds its key bounds");
                    }
                }

                if (keyCount > 0)
                {
                    _stateReadMemory?.Reserve(2 * totalKeyBytes + 128 * keyCount,
                        store.IsSystem ? null : QosContext.Current);
                }

                // The managed driver must never project unadmitted keys or hashes with unchecked storage types.
                store.Checkpoint?.Invoke("repair.keys", store.Tenant ?? ChangeGroups.System);
                var command = Command($"""
                    SELECT bucket, collection, id, h1, h2 FROM _tb_state WHERE {predicate}
                    ORDER BY {order} LIMIT $limit;
                    """);
                Bind(command);
                List<(long Bucket, string Collection, string Id, UInt128 Hash)> keys = [];
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        keys.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                            ((UInt128)unchecked((ulong)reader.GetInt64(3)) << 64) | unchecked((ulong)reader.GetInt64(4))));
                    }
                }

                foreach (var (storedBucket, collection, id, hash) in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ChangeGroups.ValidKey(store.IsSystem ? ChangeGroups.System : store.Tenant!, collection, id)
                        || storedBucket is < 0 or >= Buckets || Bucket(collection, id) != storedBucket
                        || (!all && storedBucket != bucket) || !HasTable(collection))
                    {
                        throw StoredDataCorruption.Create("a stored repair key has an invalid key, bucket or table");
                    }

                    // The keys provide count lookahead; do not decode the next whole state merely to count it.
                    if (states.Count == maxStates)
                    {
                        var last = states[^1];
                        return (states, (BucketCursor?)new BucketCursor(Bucket(last.Collection, last.Id), last.Collection, last.Id));
                    }

                    var state = ReadState(collection, id);
                    if (DocumentStateEncoding.Digest(collection, id, state) != hash)
                    {
                        throw StoredDataCorruption.Create("a stored repair state does not match its keyed digest");
                    }

                    var size = DocumentStateEncoding.Encode(state).Length;
                    var entryBytes = RepairEntryBytes(collection, id, size);
                    if (entryBytes > ReplicationProtocol.MaxApplicationBytes)
                    {
                        throw new StorageBusyException();
                    }

                    if (states.Count > 0 && (bytes + size > maxBytes
                        || pageBytes + entryBytes > ReplicationProtocol.MaxApplicationBytes))
                    {
                        var last = states[^1];
                        return (states, (BucketCursor?)new BucketCursor(Bucket(last.Collection, last.Id), last.Collection, last.Id));
                    }

                    states.Add(new RepairState(collection, id, state));
                    bytes += size;
                    pageBytes += entryBytes;
                }

                if (all)
                {
                    break;
                }

                void Bind(global::Turso.Data.Sqlite.SqliteCommand target)
                {
                    target.Parameters.AddWithValue("$bucket", (long)bucket);
                    target.Parameters.AddWithValue("$resume", resume is null ? 0 : 1);
                    target.Parameters.AddWithValue("$collection", resume?.Collection ?? string.Empty);
                    target.Parameters.AddWithValue("$id", resume?.Id ?? string.Empty);
                    target.Parameters.AddWithValue("$limit", (long)(maxStates - states.Count + 1));
                }
            }

            return (states, (BucketCursor?)null);
        }, cancellationToken);

    private static long RepairEntryBytes(string collection, string id, int stateBytes)
    {
        var collectionBytes = StrictUtf8.GetByteCount(collection);
        var idBytes = StrictUtf8.GetByteCount(id);
        var entryBytes = 1L + Google.Protobuf.CodedOutputStream.ComputeLengthSize(collectionBytes) + collectionBytes
            + 1L + Google.Protobuf.CodedOutputStream.ComputeLengthSize(idBytes) + idBytes
            + 1L + Google.Protobuf.CodedOutputStream.ComputeLengthSize(stateBytes) + stateBytes;
        if (entryBytes > int.MaxValue)
        {
            throw new StorageBusyException();
        }

        return 1L + Google.Protobuf.CodedOutputStream.ComputeLengthSize((int)entryBytes) + entryBytes;
    }

}
