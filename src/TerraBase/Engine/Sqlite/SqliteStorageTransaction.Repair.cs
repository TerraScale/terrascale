using System.Buffers.Binary;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Anti-entropy reads (docs/terrabase/multi-region-replication.md section 10): the digest of each of a database's 4096
/// buckets, the XOR of its documents' <c>_tb_state.h1/h2</c>, and the whole states of chosen buckets, paged.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    /// <summary>Buckets of a database: the first 12 bits of a document's key hash.</summary>
    internal const int Buckets = 4096;

    /// <summary>4096 × (h1, h2), little-endian.</summary>
    internal ValueTask<byte[]> ReadDigestsAsync(CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            // shortcut: reads every state row per round; keep _tb_merkle folded per commit once databases hold millions of documents.
            var digests = new byte[Buckets * 16];
            using var reader = Command("SELECT bucket, h1, h2 FROM _tb_state;").ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = digests.AsSpan((int)reader.GetInt64(0) * 16, 16);
                BinaryPrimitives.WriteInt64LittleEndian(slot, BinaryPrimitives.ReadInt64LittleEndian(slot) ^ reader.GetInt64(1));
                BinaryPrimitives.WriteInt64LittleEndian(slot[8..], BinaryPrimitives.ReadInt64LittleEndian(slot[8..]) ^ reader.GetInt64(2));
            }

            return digests;
        }, cancellationToken);

    /// <summary>
    /// The whole states of the documents in <paramref name="buckets"/> after <paramref name="after"/> (bucket, collection,
    /// id), in that order: at most <paramref name="maxStates"/>, and <paramref name="maxBytes"/> of encoded states unless the
    /// first alone is larger. The cursor of the next page, or null after the last.
    /// </summary>
    internal ValueTask<(List<RepairState> States, BucketCursor? Next)> ReadBucketStatesAsync(
        IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            List<RepairState> states = [];
            var bytes = 0;
            var ordered = buckets.Distinct().Order().ToList();
            var all = ordered.Count == Buckets && ordered[0] == 0 && ordered[^1] == Buckets - 1;
            var start = after is { } cursor ? ordered.FindIndex(bucket => bucket >= cursor.Bucket) : 0;
            for (var index = start < 0 ? ordered.Count : start; index < ordered.Count; index++)
            {
                var bucket = ordered[index];
                var resume = after is { } from && from.Bucket == bucket ? from : null;
                // Keys first, then each state: Turso refuses a write on a connection while one of its reads is open, and a
                // state read is a second statement.
                // Genesis and a complete repair read all buckets. One indexed page avoids 4096 empty-bucket queries,
                // avoiding empty-bucket work in the catalog gate's bounded startup pass.
                var command = Command(all ? """
                    SELECT collection, id FROM _tb_state
                    WHERE bucket > $bucket OR (bucket = $bucket AND (collection > $collection OR (collection = $collection AND id > $id)))
                    ORDER BY bucket, collection, id LIMIT $limit;
                    """ : """
                    SELECT collection, id FROM _tb_state WHERE bucket = $bucket AND (collection > $collection OR (collection = $collection AND id > $id))
                    ORDER BY collection, id LIMIT $limit;
                    """);
                command.Parameters.AddWithValue("$bucket", (long)bucket);
                command.Parameters.AddWithValue("$collection", resume?.Collection ?? string.Empty);
                command.Parameters.AddWithValue("$id", resume?.Id ?? string.Empty);
                command.Parameters.AddWithValue("$limit", (long)(maxStates - states.Count + 1));
                List<(string Collection, string Id)> keys = [];
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        keys.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }

                foreach (var (collection, id) in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var state = ReadState(collection, id);
                    var size = DocumentStateEncoding.Encode(state).Length;
                    if (states.Count == maxStates || (states.Count > 0 && bytes + size > maxBytes))
                    {
                        var last = states[^1];
                        return (states, (BucketCursor?)new BucketCursor(Bucket(last.Collection, last.Id), last.Collection, last.Id));
                    }

                    states.Add(new RepairState(collection, id, state));
                    bytes += size;
                }

                if (all)
                {
                    break;
                }
            }

            return (states, (BucketCursor?)null);
        }, cancellationToken);
}
