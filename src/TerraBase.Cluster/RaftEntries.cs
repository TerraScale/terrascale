using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using DotNext.Net.Cluster;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal static class RaftIdentity
{
    public static ClusterMemberId For(string nodeId)
    {
        Span<byte> hash = stackalloc byte[32];
        var maxBytes = Encoding.UTF8.GetMaxByteCount(nodeId.Length);
        Span<byte> utf8 = maxBytes <= 256 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        var written = Encoding.UTF8.GetBytes(nodeId, utf8);
        SHA256.HashData(utf8[..written], hash);
        return new ClusterMemberId(hash[..ClusterMemberId.Size]);
    }

    public static ushort ShardOf(Ulid databaseId, int shardCount)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = databaseId.TryWriteBytes(bytes);
        return (ushort)(XxHash32.HashToUInt32(bytes) % (uint)shardCount);
    }
}

internal sealed record ReplicatedCommit(Ulid DatabaseId, CommitMetadata Commit)
{
    public byte[] Encode()
    {
        var maxSize = 16 + ClusterCodec.EstimateCommitSize(Commit);
        var rented = ArrayPool<byte>.Shared.Rent(maxSize);
        try
        {
            var span = rented.AsSpan();
            if (!DatabaseId.TryWriteBytes(span))
            {
                throw new InvalidOperationException("Failed to encode database id.");
            }

            var written = 16 + ClusterCodec.WriteCommitToSpan(span[16..], Commit);
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static bool TryPeekHeader(ReadOnlySpan<byte> payload, out Ulid databaseId, out ReadOnlySpan<byte> originUtf8)
    {
        if (payload.Length < 33)
        {
            databaseId = default;
            originUtf8 = [];
            return false;
        }

        var reader = new WireReader(payload);
        databaseId = reader.ReadUlid();
        _ = reader.ReadUlid();
        originUtf8 = reader.ReadBytesSpan();
        if (reader.IsFaulted)
        {
            databaseId = default;
            originUtf8 = [];
            return false;
        }

        return true;
    }

    public static ReplicatedCommit? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        var reader = new WireReader(payload);
        var databaseId = reader.ReadUlid();
        var commit = ClusterCodec.ReadCommit(ref reader);
        return reader.IsComplete ? new ReplicatedCommit(databaseId, commit) : null;
    }
}
