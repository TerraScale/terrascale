using System.Buffers;
using DotNext.Net.Cluster;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal enum RaftVoteKind : byte
{
    Vote = 0,
    PreVote = 1,
    Resign = 2,
    Synchronize = 3,
}

internal enum RaftAppendKind : byte
{
    Append = 0,
    Propose = 1,
}

internal enum RaftProposeStatus : byte
{
    Committed = 0,
    NotLeader = 1,
    Failed = 2,
}

internal sealed record RaftWireEntry(long Term, int? CommandId, bool IsConfiguration, ReadOnlyMemory<byte> Payload);

internal static class RaftCodec
{
    public static void WriteMember(IBufferWriter<byte> writer, ClusterMemberId member)
    {
        Span<byte> buffer = stackalloc byte[ClusterMemberId.Size];
        member.Format(buffer);
        writer.WriteBytes(buffer);
    }

    public static ClusterMemberId ReadMember(ref WireReader reader)
    {
        var bytes = reader.ReadBytesSpan();
        return bytes.Length == ClusterMemberId.Size ? new ClusterMemberId(bytes) : default;
    }

    public static void WriteEntry(IBufferWriter<byte> writer, RaftWireEntry entry)
    {
        writer.WriteInt64(entry.Term);
        writer.WriteBool(entry.CommandId.HasValue);
        writer.WriteInt32(entry.CommandId.GetValueOrDefault());
        writer.WriteBool(entry.IsConfiguration);
        writer.WriteBytes(entry.Payload.Span);
    }

    public static RaftWireEntry ReadEntry(ref WireReader reader)
    {
        var term = reader.ReadInt64();
        var hasCommand = reader.ReadBool();
        var command = reader.ReadInt32();
        var configuration = reader.ReadBool();
        return new RaftWireEntry(term, hasCommand ? command : null, configuration, reader.ReadBytes());
    }
}

internal sealed record RaftVoteMessage(ushort Shard, RaftVoteKind Kind, ClusterMemberId Sender, long Term, long LastLogIndex, long LastLogTerm, int StateVersion) : IBinaryMessage<RaftVoteMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUInt16(Shard);
        writer.WriteUInt8((byte)Kind);
        RaftCodec.WriteMember(writer, Sender);
        writer.WriteInt64(Term);
        writer.WriteInt64(LastLogIndex);
        writer.WriteInt64(LastLogTerm);
        writer.WriteInt32(StateVersion);
    }

    public static RaftVoteMessage ReadFrom(ref WireReader reader)
    {
        var shard = reader.ReadUInt16();
        var kind = (RaftVoteKind)reader.ReadUInt8();
        var sender = RaftCodec.ReadMember(ref reader);
        var term = reader.ReadInt64();
        var lastLogIndex = reader.ReadInt64();
        var lastLogTerm = reader.ReadInt64();
        return new RaftVoteMessage(shard, kind, sender, term, lastLogIndex, lastLogTerm, reader.ReadInt32());
    }
}

internal sealed record RaftVoteResponse(long Term, long Value, bool HasValue) : IBinaryMessage<RaftVoteResponse>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteInt64(Term);
        writer.WriteInt64(Value);
        writer.WriteBool(HasValue);
    }

    public static RaftVoteResponse ReadFrom(ref WireReader reader)
    {
        var term = reader.ReadInt64();
        var value = reader.ReadInt64();
        return new RaftVoteResponse(term, value, reader.ReadBool());
    }
}

internal sealed record RaftAppendMessage(
    ushort Shard,
    RaftAppendKind Kind,
    ClusterMemberId Sender,
    long Term,
    long PrevLogIndex,
    long PrevLogTerm,
    long CommitIndex,
    int StateVersion,
    IReadOnlyList<RaftWireEntry> Entries) : IBinaryMessage<RaftAppendMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUInt16(Shard);
        writer.WriteUInt8((byte)Kind);
        RaftCodec.WriteMember(writer, Sender);
        writer.WriteInt64(Term);
        writer.WriteInt64(PrevLogIndex);
        writer.WriteInt64(PrevLogTerm);
        writer.WriteInt64(CommitIndex);
        writer.WriteInt32(StateVersion);
        var count = Entries.Count;
        writer.WriteVarUInt64((ulong)count);
        for (var index = 0; index < count; index++)
        {
            RaftCodec.WriteEntry(writer, Entries[index]);
        }
    }

    public static RaftAppendMessage ReadFrom(ref WireReader reader)
    {
        var shard = reader.ReadUInt16();
        var kind = (RaftAppendKind)reader.ReadUInt8();
        var sender = RaftCodec.ReadMember(ref reader);
        var term = reader.ReadInt64();
        var prevLogIndex = reader.ReadInt64();
        var prevLogTerm = reader.ReadInt64();
        var commitIndex = reader.ReadInt64();
        var stateVersion = reader.ReadInt32();
        var count = reader.ReadLength();
        IReadOnlyList<RaftWireEntry> entries;
        if (count == 0)
        {
            entries = [];
        }
        else
        {
            var array = new RaftWireEntry[count];
            var read = 0;
            for (; read < count && !reader.IsFaulted; read++)
            {
                array[read] = RaftCodec.ReadEntry(ref reader);
            }

            entries = read == count ? array : array[..read];
        }

        return new RaftAppendMessage(shard, kind, sender, term, prevLogIndex, prevLogTerm, commitIndex, stateVersion, entries);
    }
}

internal sealed record RaftAppendResponse(long Term, byte Heartbeat, long LastIndex, RaftProposeStatus Status) : IBinaryMessage<RaftAppendResponse>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteInt64(Term);
        writer.WriteUInt8(Heartbeat);
        writer.WriteInt64(LastIndex);
        writer.WriteUInt8((byte)Status);
    }

    public static RaftAppendResponse ReadFrom(ref WireReader reader)
    {
        var term = reader.ReadInt64();
        var result = reader.ReadUInt8();
        var lastIndex = reader.ReadInt64();
        return new RaftAppendResponse(term, result, lastIndex, (RaftProposeStatus)reader.ReadUInt8());
    }
}

internal sealed record RaftSnapshotMessage(ushort Shard, ClusterMemberId Sender, long Term, long SnapshotIndex, int StateVersion, RaftWireEntry Snapshot) : IBinaryMessage<RaftSnapshotMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUInt16(Shard);
        RaftCodec.WriteMember(writer, Sender);
        writer.WriteInt64(Term);
        writer.WriteInt64(SnapshotIndex);
        writer.WriteInt32(StateVersion);
        RaftCodec.WriteEntry(writer, Snapshot);
    }

    public static RaftSnapshotMessage ReadFrom(ref WireReader reader)
    {
        var shard = reader.ReadUInt16();
        var sender = RaftCodec.ReadMember(ref reader);
        var term = reader.ReadInt64();
        var snapshotIndex = reader.ReadInt64();
        var stateVersion = reader.ReadInt32();
        return new RaftSnapshotMessage(shard, sender, term, snapshotIndex, stateVersion, RaftCodec.ReadEntry(ref reader));
    }
}
