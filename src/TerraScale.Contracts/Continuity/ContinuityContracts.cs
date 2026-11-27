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

    void ObserveBookmark(Ulid databaseId, CausalBookmark bookmark)
    {
    }
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

public sealed record ArchiveShredded;

public sealed record ContinuityNotFound(string Message, string Code = "not_found");

public sealed record ContinuityInvalidTarget(string Message, string Code = "invalid_request");

public sealed record ContinuityConflict(string Message, string Code = "conflict");

public sealed record ContinuityUnavailable(string Message, string Code = "unavailable");

public readonly union BackupStatusOutcome(BackupStatus, ContinuityNotFound, ContinuityUnavailable);

public readonly union RestoreStartOutcome(RestoreOperation, ContinuityNotFound, ContinuityInvalidTarget, ContinuityConflict, ContinuityUnavailable);

public readonly union RestoreOperationOutcome(RestoreOperation, ContinuityNotFound);

public readonly union CryptoShredBackupOutcome(ArchiveShredded, ContinuityNotFound, ContinuityUnavailable);

public interface IBackupContinuity
{
    ValueTask<BackupStatusOutcome> GetStatusAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask<BackupStatusOutcome> ArchiveNowAsync(Ulid databaseId, CancellationToken cancellationToken);

    ValueTask<RestoreStartOutcome> RestoreAsync(PointInTimeRestoreRequest request, CancellationToken cancellationToken);

    ValueTask<RestoreStartOutcome> RewindAsync(Ulid databaseId, RestoreTarget target, CancellationToken cancellationToken);

    ValueTask<RestoreStartOutcome> BranchAsync(BranchRequest request, CancellationToken cancellationToken);

    ValueTask<RestoreOperationOutcome> GetOperationAsync(Ulid operationId, CancellationToken cancellationToken);

    ValueTask<CryptoShredBackupOutcome> CryptoShredAsync(Ulid databaseId, CancellationToken cancellationToken);
}
