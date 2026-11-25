using Microsoft.Extensions.Logging;

namespace TerraBase.Continuity;

internal static partial class ContinuityLog
{
    [LoggerMessage(EventId = 2100, Level = LogLevel.Critical, Message = "Continuous backup is disabled on this node; no new recovery points are stored")]
    public static partial void BackupDisabled(ILogger logger);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "Archiving database {DatabaseId} failed: {Reason}")]
    public static partial void ArchiveFailed(ILogger logger, Ulid databaseId, string reason);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Error, Message = "Archiving database {DatabaseId} faulted")]
    public static partial void ArchiveFaulted(ILogger logger, Exception exception, Ulid databaseId);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Error, Message = "Listing active databases for archiving faulted")]
    public static partial void ArchivePassFaulted(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Retention policy for database {DatabaseId} could not be read: {Reason}")]
    public static partial void RetentionPolicyUnavailable(ILogger logger, Ulid databaseId, string reason);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Error, Message = "Removing invalid archive retirement record {Key}: {Reason}")]
    public static partial void RetirementCorrupted(ILogger logger, string key, string reason);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Warning, Message = "Archive object cleanup for database {DatabaseId} is deferred: {Reason}")]
    public static partial void RetirementDeferred(ILogger logger, Ulid databaseId, string reason);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Warning, Message = "Local archive pruning for database {DatabaseId} is waiting for replica progress through {Prunable}; confirmed through {Confirmed}")]
    public static partial void ReplicaRetentionHeld(ILogger logger, Ulid databaseId, CausalBookmark prunable, CausalBookmark confirmed);

    [LoggerMessage(EventId = 2110, Level = LogLevel.Information, Message = "Continuity operation {OperationId} ({Kind}) finished as {State} for {TargetDatabaseId}")]
    public static partial void OperationFinished(ILogger logger, Ulid operationId, string kind, string state, Ulid targetDatabaseId);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Warning, Message = "Continuity operation {OperationId} failed: {Reason}")]
    public static partial void OperationFailed(ILogger logger, Ulid operationId, string reason);

    [LoggerMessage(EventId = 2112, Level = LogLevel.Error, Message = "Continuity operation {OperationId} faulted")]
    public static partial void OperationFaulted(ILogger logger, Exception exception, Ulid operationId);

    [LoggerMessage(EventId = 2113, Level = LogLevel.Warning, Message = "Recording continuity operation {OperationId} in the archive manifest failed: {Reason}")]
    public static partial void OperationRecordFailed(ILogger logger, Ulid operationId, string reason);

    [LoggerMessage(EventId = 2114, Level = LogLevel.Warning, Message = "Removing stale continuity staging files failed: {Reason}")]
    public static partial void StagingCleanupFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 2120, Level = LogLevel.Warning, Message = "Changefeed replay for database {DatabaseId} origin {Origin} stopped: {Reason}")]
    public static partial void ReplayStopped(ILogger logger, Ulid databaseId, string origin, string reason);
}
