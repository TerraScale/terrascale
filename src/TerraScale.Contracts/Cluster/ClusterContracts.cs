using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Storage;

namespace TerraScale.Contracts.Cluster;

public sealed record PeerNodeInfo(string NodeId, string Region, string Host, int QuicPort, int InternalHttpPort);

public enum PeerStatus
{
    Healthy,
    Suspect,
    Unreachable,
}

public sealed record PeerHealth(PeerNodeInfo Peer, PeerStatus Status, TimeSpan LastRoundTrip, DateTimeOffset LastSeenUtc);

public sealed record StrongPreparePayload(
    Ulid DatabaseId,
    Ulid TxId,
    string OriginRegion,
    HlcTimestamp Priority,
    HlcTimestamp ReadSnapshot,
    ulong SchemaEpoch,
    IReadOnlyList<ReadKey> ReadSet,
    IReadOnlyList<string> ScannedTables,
    IReadOnlyList<RowKey> WriteKeys,
    bool IsSchemaChange);

public sealed record Prepared(Ulid TxId, string Region, HlcTimestamp PreparedAt);

public sealed record Wounded(Ulid TxId, Ulid WoundedBy, HlcTimestamp WinnerPriority);

public sealed record ReadSetStale(Ulid TxId, IReadOnlyList<RowKey> StaleKeys, IReadOnlyList<string> StaleTables);

public sealed record SchemaEpochStale(Ulid TxId, ulong ExpectedEpoch, ulong CurrentEpoch);

public readonly union StrongPrepareOutcome(Prepared, Wounded, ReadSetStale, SchemaEpochStale);

public sealed record StrongCommitPayload(Ulid DatabaseId, CommitMetadata Commit);

public sealed record StrongWoundPayload(Ulid DatabaseId, Ulid TxId, Ulid WoundedBy, HlcTimestamp WinnerPriority);

public sealed record SerializationConflict(Ulid DatabaseId, Ulid TxId, string Reason);

public interface IClusterCoordinator
{
    string LocalRegion { get; }

    string LocalNodeId { get; }

    IReadOnlyList<PeerHealth> Peers { get; }

    CausalBookmark CurrentBookmark(Ulid databaseId);

    ValueTask<Result<Done, NotFound, RequestTimeout>> WaitForBookmarkAsync(Ulid databaseId, CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken);

    ValueTask<Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> ExecuteWriteAsync(
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitTransactionAsync(
        ISqlDatabaseSession session,
        ISqlTransaction transaction,
        CancellationToken cancellationToken);
}
