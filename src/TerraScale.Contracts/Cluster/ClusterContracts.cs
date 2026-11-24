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

public sealed record ClusterUnavailable(string Message, string Code = "unavailable");

public sealed record ClusterTimeout(string Message, string Code = "request_timeout");

public readonly union ClusterWaitBookmarkOutcome(BookmarkReached, DatabaseNotFound, ClusterTimeout);

public readonly union ClusterWriteOutcome(SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, ClusterUnavailable, ClusterTimeout, StorageBusy);

public readonly union ClusterBeginTransactionOutcome(ISqlTransaction, StorageBusy, ClusterTimeout);

public readonly union ClusterCommitTransactionOutcome(CommitMetadata, SerializationConflict, SchemaMismatch, ClusterUnavailable, ClusterTimeout, StorageBusy);

public interface IClusterCoordinator
{
    string LocalRegion { get; }

    string LocalNodeId { get; }

    IReadOnlyList<PeerHealth> Peers { get; }

    void TrackDatabase(Ulid databaseId);

    void UntrackDatabase(Ulid databaseId);

    CausalBookmark CurrentBookmark(Ulid databaseId);

    ValueTask<ClusterWaitBookmarkOutcome> WaitForBookmarkAsync(Ulid databaseId, CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken);

    ValueTask<ClusterWriteOutcome> ExecuteWriteAsync(
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken);

    ValueTask<ClusterWriteOutcome> ExecuteLocalBootstrapWriteAsync(
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken);

    ValueTask<ClusterBeginTransactionOutcome> BeginTransactionAsync(ISqlDatabaseSession session, SqlTransactionOptions options, CancellationToken cancellationToken);

    ValueTask<ClusterCommitTransactionOutcome> CommitTransactionAsync(
        ISqlDatabaseSession session,
        ISqlTransaction transaction,
        CancellationToken cancellationToken);
}
