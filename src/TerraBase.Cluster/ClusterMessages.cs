using System.Buffers;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal sealed record PingMessage(string NodeId, string Region, long SentAtTicks, long InstanceId) : IBinaryMessage<PingMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteString(NodeId);
        writer.WriteString(Region);
        writer.WriteInt64(SentAtTicks);
        writer.WriteInt64(InstanceId);
    }

    public static PingMessage ReadFrom(ref WireReader reader)
    {
        var nodeId = reader.ReadString();
        var region = reader.ReadString();
        var sentAtTicks = reader.ReadInt64();
        return new PingMessage(nodeId, region, sentAtTicks, reader.ReadInt64());
    }
}

internal sealed record PongMessage(string NodeId, string Region, long SentAtTicks, long InstanceId) : IBinaryMessage<PongMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteString(NodeId);
        writer.WriteString(Region);
        writer.WriteInt64(SentAtTicks);
        writer.WriteInt64(InstanceId);
    }

    public static PongMessage ReadFrom(ref WireReader reader)
    {
        var nodeId = reader.ReadString();
        var region = reader.ReadString();
        var sentAtTicks = reader.ReadInt64();
        return new PongMessage(nodeId, region, sentAtTicks, reader.ReadInt64());
    }
}

internal readonly record struct DeltaPushMessage(Ulid DatabaseId, string SenderNodeId, IReadOnlyList<CommitMetadata> Commits, bool AckRequested) : IBinaryMessage<DeltaPushMessage>
{
    public void WriteTo(IBufferWriter<byte> writer) =>
        ClusterCodec.WriteDeltaPush(writer, DatabaseId, SenderNodeId, Commits, AckRequested);

    public static DeltaPushMessage ReadFrom(ref WireReader reader)
    {
        var databaseId = reader.ReadUlid();
        var sender = ClusterCodec.ReadPooledString(ref reader);
        var commits = ClusterCodec.ReadCommits(ref reader);
        return new DeltaPushMessage(databaseId, sender, commits, reader.ReadBool());
    }
}

internal readonly record struct DeltaAckMessage(Ulid DatabaseId, string NodeId, CausalBookmark Bookmark) : IBinaryMessage<DeltaAckMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUlid(DatabaseId);
        writer.WriteString(NodeId);
        writer.WriteBookmark(Bookmark);
    }

    public static DeltaAckMessage ReadFrom(ref WireReader reader)
    {
        var databaseId = reader.ReadUlid();
        var nodeId = ClusterCodec.ReadPooledString(ref reader);
        return new DeltaAckMessage(databaseId, nodeId, ClusterCodec.ReadBookmark(ref reader));
    }
}

internal sealed record CatchUpRequestMessage(Ulid DatabaseId, string RequesterNodeId, CausalBookmark Have, int MaxEntries) : IBinaryMessage<CatchUpRequestMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUlid(DatabaseId);
        writer.WriteString(RequesterNodeId);
        writer.WriteBookmark(Have);
        writer.WriteVarUInt64((ulong)MaxEntries);
    }

    public static CatchUpRequestMessage ReadFrom(ref WireReader reader)
    {
        var databaseId = reader.ReadUlid();
        var requester = reader.ReadString();
        var have = ClusterCodec.ReadBookmark(ref reader);
        return new CatchUpRequestMessage(databaseId, requester, have, (int)Math.Min(reader.ReadVarUInt64(), int.MaxValue));
    }
}

internal sealed record CatchUpResponseMessage(Ulid DatabaseId, bool Found, CausalBookmark Bookmark, IReadOnlyList<CommitMetadata> Entries, bool HasMore) : IBinaryMessage<CatchUpResponseMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUlid(DatabaseId);
        writer.WriteBool(Found);
        writer.WriteBookmark(Bookmark);
        writer.WriteCommits(Entries);
        writer.WriteBool(HasMore);
    }

    public static CatchUpResponseMessage ReadFrom(ref WireReader reader)
    {
        var databaseId = reader.ReadUlid();
        var found = reader.ReadBool();
        var bookmark = ClusterCodec.ReadBookmark(ref reader);
        var entries = ClusterCodec.ReadCommits(ref reader);
        return new CatchUpResponseMessage(databaseId, found, bookmark, entries, reader.ReadBool());
    }
}

internal sealed record StrongPrepareMessage(StrongPreparePayload Payload, string CoordinatorNodeId, CausalBookmark OriginBookmark) : IBinaryMessage<StrongPrepareMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUlid(Payload.DatabaseId);
        writer.WriteUlid(Payload.TxId);
        writer.WriteString(Payload.OriginRegion);
        writer.WriteHlc(Payload.Priority);
        writer.WriteHlc(Payload.ReadSnapshot);
        writer.WriteVarUInt64(Payload.SchemaEpoch);
        writer.WriteReadKeys(Payload.ReadSet);
        writer.WriteStringList(Payload.ScannedTables);
        writer.WriteRowKeys(Payload.WriteKeys);
        writer.WriteBool(Payload.IsSchemaChange);
        writer.WriteString(CoordinatorNodeId);
        writer.WriteBookmark(OriginBookmark);
    }

    public static StrongPrepareMessage ReadFrom(ref WireReader reader)
    {
        var databaseId = reader.ReadUlid();
        var txId = reader.ReadUlid();
        var origin = reader.ReadString();
        var priority = ClusterCodec.ReadHlc(ref reader);
        var snapshot = ClusterCodec.ReadHlc(ref reader);
        var epoch = reader.ReadVarUInt64();
        var readSet = ClusterCodec.ReadReadKeys(ref reader);
        var scanned = reader.ReadStringList();
        var writeKeys = ClusterCodec.ReadRowKeys(ref reader);
        var schemaChange = reader.ReadBool();
        var coordinator = reader.ReadString();
        var bookmark = ClusterCodec.ReadBookmark(ref reader);
        return new StrongPrepareMessage(new StrongPreparePayload(databaseId, txId, origin, priority, snapshot, epoch, readSet, scanned, writeKeys, schemaChange), coordinator, bookmark);
    }
}

internal sealed record StrongPrepareResponseMessage(StrongPrepareOutcome Outcome) : IBinaryMessage<StrongPrepareResponseMessage>
{
    private const byte PreparedTag = 0;
    private const byte WoundedTag = 1;
    private const byte ReadSetStaleTag = 2;
    private const byte SchemaEpochStaleTag = 3;

    public void WriteTo(IBufferWriter<byte> writer)
    {
        _ = Outcome switch
        {
            Prepared prepared => WritePrepared(writer, prepared),
            Wounded wounded => WriteWounded(writer, wounded),
            ReadSetStale stale => WriteStale(writer, stale),
            SchemaEpochStale epoch => WriteEpoch(writer, epoch),
        };
    }

    private static bool WritePrepared(IBufferWriter<byte> writer, Prepared prepared)
    {
        writer.WriteUInt8(PreparedTag);
        writer.WriteUlid(prepared.TxId);
        writer.WriteString(prepared.Region);
        writer.WriteHlc(prepared.PreparedAt);
        return true;
    }

    private static bool WriteWounded(IBufferWriter<byte> writer, Wounded wounded)
    {
        writer.WriteUInt8(WoundedTag);
        writer.WriteUlid(wounded.TxId);
        writer.WriteUlid(wounded.WoundedBy);
        writer.WriteHlc(wounded.WinnerPriority);
        return true;
    }

    private static bool WriteStale(IBufferWriter<byte> writer, ReadSetStale stale)
    {
        writer.WriteUInt8(ReadSetStaleTag);
        writer.WriteUlid(stale.TxId);
        writer.WriteRowKeys(stale.StaleKeys);
        writer.WriteStringList(stale.StaleTables);
        return true;
    }

    private static bool WriteEpoch(IBufferWriter<byte> writer, SchemaEpochStale epoch)
    {
        writer.WriteUInt8(SchemaEpochStaleTag);
        writer.WriteUlid(epoch.TxId);
        writer.WriteVarUInt64(epoch.ExpectedEpoch);
        writer.WriteVarUInt64(epoch.CurrentEpoch);
        return true;
    }

    public static StrongPrepareResponseMessage ReadFrom(ref WireReader reader)
    {
        var tag = reader.ReadUInt8();
        var txId = reader.ReadUlid();
        StrongPrepareOutcome outcome = tag switch
        {
            PreparedTag => ReadPrepared(ref reader, txId),
            WoundedTag => ReadWounded(ref reader, txId),
            ReadSetStaleTag => ReadStale(ref reader, txId),
            _ => ReadEpoch(ref reader, txId),
        };
        return new StrongPrepareResponseMessage(outcome);
    }

    private static Prepared ReadPrepared(ref WireReader reader, Ulid txId)
    {
        var region = reader.ReadString();
        return new Prepared(txId, region, ClusterCodec.ReadHlc(ref reader));
    }

    private static Wounded ReadWounded(ref WireReader reader, Ulid txId)
    {
        var woundedBy = reader.ReadUlid();
        return new Wounded(txId, woundedBy, ClusterCodec.ReadHlc(ref reader));
    }

    private static ReadSetStale ReadStale(ref WireReader reader, Ulid txId)
    {
        var keys = ClusterCodec.ReadRowKeys(ref reader);
        return new ReadSetStale(txId, keys, reader.ReadStringList());
    }

    private static SchemaEpochStale ReadEpoch(ref WireReader reader, Ulid txId)
    {
        var expected = reader.ReadVarUInt64();
        return new SchemaEpochStale(txId, expected, reader.ReadVarUInt64());
    }
}

internal sealed record StrongCommitMessage(StrongCommitPayload Payload, string SenderNodeId) : IBinaryMessage<StrongCommitMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUlid(Payload.DatabaseId);
        writer.WriteCommit(Payload.Commit);
        writer.WriteString(SenderNodeId);
    }

    public static StrongCommitMessage ReadFrom(ref WireReader reader)
    {
        var databaseId = reader.ReadUlid();
        var commit = ClusterCodec.ReadCommit(ref reader);
        return new StrongCommitMessage(new StrongCommitPayload(databaseId, commit), reader.ReadString());
    }
}

internal sealed record StrongWoundMessage(StrongWoundPayload Payload) : IBinaryMessage<StrongWoundMessage>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        writer.WriteUlid(Payload.DatabaseId);
        writer.WriteUlid(Payload.TxId);
        writer.WriteUlid(Payload.WoundedBy);
        writer.WriteHlc(Payload.WinnerPriority);
    }

    public static StrongWoundMessage ReadFrom(ref WireReader reader)
    {
        var databaseId = reader.ReadUlid();
        var txId = reader.ReadUlid();
        var woundedBy = reader.ReadUlid();
        return new StrongWoundMessage(new StrongWoundPayload(databaseId, txId, woundedBy, ClusterCodec.ReadHlc(ref reader)));
    }
}

internal sealed record StrongWoundResponseMessage(WoundDecision Decision) : IBinaryMessage<StrongWoundResponseMessage>
{
    public void WriteTo(IBufferWriter<byte> writer) => writer.WriteUInt8((byte)Decision);

    public static StrongWoundResponseMessage ReadFrom(ref WireReader reader) => new((WoundDecision)reader.ReadUInt8());
}
