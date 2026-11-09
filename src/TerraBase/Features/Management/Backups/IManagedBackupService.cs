using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Backups;

public sealed record DatabaseBackup(string Id, string DatabaseId, string Status, string Trigger, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record BackupPolicy(string? FullBackupCron, int RetentionDays, DateTimeOffset? UpdatedAt);

public sealed record BackupPolicyError(string Code, string Message);

public sealed record BackupPolicyStatus(
    string? FullBackupCron,
    int RetentionDays,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? LastFailedAt,
    BackupPolicyError? LastError,
    DateTimeOffset? NextDueAt);

public sealed record BackupPolicyRequest(string? FullBackupCron, int? RetentionDays);

public sealed record PointInTimeRestoreRequest(DateTimeOffset? PointInTime, string? Name, string? Destination = null, long? TtlSeconds = null);

public sealed record PointInTimeRewindRequest(DateTimeOffset? PointInTime, string? Destination = null);

public readonly union ListBackupsResult(ManagementPage<DatabaseBackup>, Unavailable);

public readonly union CreateBackupResult(DatabaseBackup, NotFound, Unavailable);

public readonly union GetBackupPolicyResult(BackupPolicyStatus, Unavailable);

public readonly union SetBackupPolicyResult(BackupPolicy, NotFound, Forbidden, Unavailable);

public readonly union RestoreBackupResult(ManagedDatabase, BackupElsewhere, NotFound, Conflict, Forbidden, Validation, QuotaExceeded, Unavailable);

public sealed record BackupElsewhere(string Region, ManagedDatabase Database);

public readonly union StartRestoreResult(RestoreOperationRecord, BackupElsewhere, NotFound, Conflict, Forbidden, Validation, QuotaExceeded, Unavailable);

public readonly union GetRestoreOperationResult(RestoreOperationRecord, NotFound, Unavailable);

public interface IManagedBackupService
{
    ValueTask<ListBackupsResult> ListBackupsAsync(string organizationId, string databaseId, string? after, int limit, CancellationToken cancellationToken);

    ValueTask<CreateBackupResult> CreateBackupAsync(string userId, string organizationId, TenantContext database, CancellationToken cancellationToken);

    ValueTask<GetBackupPolicyResult> GetPolicyAsync(string databaseId, CancellationToken cancellationToken);

    ValueTask<SetBackupPolicyResult> SetPolicyAsync(
        string userId, string organizationId, TenantContext database, string? fullBackupCron, int retentionDays, CancellationToken cancellationToken);

    ValueTask<StartRestoreResult> StartRestoreAsync(
        string userId, string organizationId, TenantContext database, string backupId, string? name, CancellationToken cancellationToken);

    ValueTask<StartRestoreResult> StartRestorePointInTimeAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string? name, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform,
        long? ttlSeconds = null);

    ValueTask<GetRestoreOperationResult> GetRestoreOperationAsync(string organizationId, string databaseId, string operationId, CancellationToken cancellationToken);

    ValueTask<RestoreBackupResult> RewindPointInTimeAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string destination, CancellationToken cancellationToken);

    ValueTask RunScheduleAsync(CancellationToken cancellationToken);

    ValueTask RecoverInterruptedRestoresAsync(CancellationToken cancellationToken);
}
