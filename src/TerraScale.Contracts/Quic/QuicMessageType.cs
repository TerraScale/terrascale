namespace TerraScale.Contracts.Quic;

public static class QuicMessageType
{
    public const ushort Ping = 0x0001;
    public const ushort Pong = 0x0002;
    public const ushort DeltaPush = 0x0100;
    public const ushort DeltaAck = 0x0101;
    public const ushort CatchUpRequest = 0x0102;
    public const ushort CatchUpResponse = 0x0103;
    public const ushort StrongPrepare = 0x0200;
    public const ushort StrongPrepareResponse = 0x0201;
    public const ushort StrongCommit = 0x0202;
    public const ushort StrongWound = 0x0203;
    public const ushort RaftAppendEntries = 0x0300;
    public const ushort RaftAppendEntriesResponse = 0x0301;
    public const ushort RaftRequestVote = 0x0302;
    public const ushort RaftRequestVoteResponse = 0x0303;
    public const ushort RaftInstallSnapshot = 0x0304;
    public const ushort RaftInstallSnapshotResponse = 0x0305;
    public const ushort ChangefeedSubscribe = 0x0400;
    public const ushort ChangefeedItem = 0x0401;
    public const ushort InternalSqlExecute = 0x0410;
    public const ushort InternalSqlResponse = 0x0411;
    public const ushort ControlSnapshotSync = 0x0500;
    public const ushort ControlSnapshotAck = 0x0501;
    public const ushort RewindDatabase = 0x0510;
    public const ushort RewindDatabaseAck = 0x0511;
}
