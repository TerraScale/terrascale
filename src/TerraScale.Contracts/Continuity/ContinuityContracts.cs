using TerraScale.Contracts.Consistency;

namespace TerraScale.Contracts.Continuity;

public sealed record ChangefeedEntry(
    Ulid DatabaseId,
    Ulid TxId,
    string OriginRegion,
    long Sequence,
    ulong SchemaEpoch,
    ConsistencyMode Mode,
    ReadOnlyMemory<byte> DeltaPayload,
    DateTimeOffset CommittedAtUtc);

public sealed record ChangefeedSubscription(Ulid DatabaseId, CausalBookmark After);

public interface IChangefeedHub
{
    ValueTask PublishAsync(ChangefeedEntry entry, CancellationToken cancellationToken);

    IAsyncEnumerable<ChangefeedEntry> SubscribeAsync(ChangefeedSubscription subscription, CancellationToken cancellationToken);
}

public readonly union RestoreTarget(DateTimeOffset, CausalBookmark);

public enum RestoreOperationState
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

public sealed record BackupStatus(
    Ulid DatabaseId,
    CausalBookmark ArchivedThrough,
    DateTimeOffset EarliestRestorePointUtc,
    DateTimeOffset LatestRestorePointUtc,
    DateTimeOffset LastSnapshotAtUtc,
    long ArchivedBytes,
    int PendingSegments);

public sealed record RestoreOperation(
    Ulid OperationId,
    Ulid SourceDatabaseId,
    Ulid TargetDatabaseId,
    RestoreOperationState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? FailureMessage);

public sealed record PointInTimeRestoreRequest(Ulid SourceDatabaseId, Ulid TargetDatabaseId, RestoreTarget Target);

public sealed record BranchRequest(Ulid SourceDatabaseId, Ulid BranchDatabaseId, RestoreTarget Target);

public interface IBackupContinuity
{
    ValueTask<Result<BackupStatus, NotFound, Unavailable>> GetStatusAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask<Result<BackupStatus, NotFound, Unavailable>> ArchiveNowAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> RestoreAsync(PointInTimeRestoreRequest request, CancellationToken cancellationToken);

    ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> RewindAsync(Ulid databaseId, RestoreTarget target, CancellationToken cancellationToken);

    ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> BranchAsync(BranchRequest request, CancellationToken cancellationToken);

    ValueTask<Result<RestoreOperation, NotFound>> GetOperationAsync(Ulid operationId, CancellationToken cancellationToken);

    ValueTask<Result<Done, NotFound, Unavailable>> CryptoShredAsync(Ulid databaseId, CancellationToken cancellationToken);
}
