using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;

namespace TerraBase.Features.Replication;

// A fixed-depth tree bounds metadata independently of the number of keys.
internal static class MerkleTree
{
    public const int Leaves = 16;
    public const int DigestBytes = (Leaves + 1) * SHA256.HashSizeInBytes;

    public static int Range(DocumentChange change) => SHA256.HashData(Key(change))[0] >> 4;

    private static byte[] Key(DocumentChange change)
    {
        var collection = Encoding.UTF8.GetBytes(change.Collection);
        var id = Encoding.UTF8.GetBytes(change.Id);
        var key = new byte[8 + collection.Length + id.Length];
        BinaryPrimitives.WriteInt32LittleEndian(key, collection.Length);
        collection.CopyTo(key, 4);
        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(4 + collection.Length), id.Length);
        id.CopyTo(key, 8 + collection.Length);
        return key;
    }

    public static async Task<byte[]> BuildAsync(IDatabaseStore store, Func<IReadOnlyList<DocumentChange>, CancellationToken, Task> repair,
        TimeProvider time, CancellationToken cancellationToken)
    {
        using var progress = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progress.Token);
        var token = deadline.Token;
        var hashes = Enumerable.Range(0, Leaves).Select(_ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256)).ToArray();
        try
        {
            foreach (var hash in hashes)
            {
                hash.AppendData("TerraBase/anti-entropy/alpha/leaf\0"u8);
            }

            var read = await store.BeginTransactionAsync(write: false, token).ConfigureAwait(false);
            await using (read.ConfigureAwait(false))
            {
                string? collection = null;
                string? id = null;
                while (true)
                {
                    progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
                    var page = await read.ReadReplicationStateAsync(collection, id, token).ConfigureAwait(false);
                    if (page.LocalRepairs.Count != 0)
                    {
                        progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
                        await repair(page.LocalRepairs, token).ConfigureAwait(false);
                    }

                    foreach (var change in page.Changes)
                    {
                        token.ThrowIfCancellationRequested();
                        // Include key, version, tombstone marker and exact body bytes.
                        hashes[Range(change)].AppendData(ChangeGroup.Encode([change]));
                    }

                    if (!page.HasMore)
                    {
                        break;
                    }

                    (collection, id) = (page.AfterCollection, page.AfterId);
                }
            }

            var digest = new byte[DigestBytes];
            var level = hashes.Select(hash => hash.GetHashAndReset()).ToArray();
            for (var i = 0; i < Leaves; i++)
            {
                level[i].CopyTo(digest, (i + 1) * SHA256.HashSizeInBytes);
            }

            while (level.Length > 1)
            {
                level = [.. Enumerable.Range(0, level.Length / 2).Select(i =>
                {
                    using var parent = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    parent.AppendData("TerraBase/anti-entropy/alpha/branch\0"u8);
                    parent.AppendData(level[i * 2]);
                    parent.AppendData(level[(i * 2) + 1]);
                    return parent.GetHashAndReset();
                })];
            }

            level[0].CopyTo(digest, 0);
            return digest;
        }
        finally
        {
            foreach (var hash in hashes)
            {
                hash.Dispose();
            }
        }
    }

    public static uint Mismatches(ReadOnlySpan<byte> local, ReadOnlySpan<byte> remote)
    {
        if (local.Length != DigestBytes || remote.Length != DigestBytes)
        {
            throw new InvalidDataException("a Merkle digest requires a root and sixteen leaves");
        }

        uint ranges = 0;
        for (var i = 0; i < Leaves; i++)
        {
            var offset = (i + 1) * SHA256.HashSizeInBytes;
            if (!local.Slice(offset, SHA256.HashSizeInBytes).SequenceEqual(remote.Slice(offset, SHA256.HashSizeInBytes)))
            {
                ranges |= 1U << i;
            }
        }

        if (ranges == 0 != local[..SHA256.HashSizeInBytes].SequenceEqual(remote[..SHA256.HashSizeInBytes]))
        {
            throw new InvalidDataException("the Merkle root and leaves disagree");
        }

        return ranges;
    }
}
