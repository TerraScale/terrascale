using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using DotNext.IO;
using DotNext.IO.Log;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal static class RaftIdentity
{
    public static ClusterMemberId For(string nodeId)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(nodeId), hash);
        return new ClusterMemberId(hash[..ClusterMemberId.Size]);
    }

    public static ushort ShardOf(Ulid databaseId, int shardCount)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = databaseId.TryWriteBytes(bytes);
        return (ushort)(XxHash32.HashToUInt32(bytes) % (uint)shardCount);
    }
}

internal readonly struct RaftPayloadEntry(long term, int? commandId, bool isConfiguration, bool isSnapshot, ReadOnlyMemory<byte> payload) : IRaftLogEntry
{
    public long Term => term;

    public int? CommandId => commandId;

    public bool IsConfiguration => isConfiguration;

    public bool IsSnapshot => isSnapshot;

    public bool IsReusable => true;

    public long? Length => payload.Length;

    public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = payload;
        return true;
    }

    public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter =>
        writer.WriteAsync(payload, null, token);
}

internal sealed class RaftEntryProducer(IReadOnlyList<RaftWireEntry> entries) : ILogEntryProducer<RaftPayloadEntry>
{
    private int _index = -1;

    public RaftPayloadEntry Current => Wrap(entries[_index]);

    public long RemainingCount => entries.Count - _index - 1;

    public ValueTask<bool> MoveNextAsync()
    {
        if (_index + 1 >= entries.Count)
        {
            return ValueTask.FromResult(false);
        }

        _index++;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static RaftPayloadEntry Wrap(RaftWireEntry entry) => new(entry.Term, entry.CommandId, entry.IsConfiguration, false, entry.Payload);
}

internal sealed record ReplicatedCommit(Ulid DatabaseId, CommitMetadata Commit)
{
    public byte[] Encode()
    {
        var writer = new ArrayBufferWriter<byte>(256 + Commit.DeltaPayload.Length);
        writer.WriteUlid(DatabaseId);
        writer.WriteCommit(Commit);
        return writer.WrittenSpan.ToArray();
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
