using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Backups;

internal static class BackupState
{
    public const string Pending = "pending";

    public const string Snapshotting = "snapshotting";

    public const string Registered = "registered";

    public const string Expiring = "expiring";
}

internal sealed record BackupRecord(
    string Id,
    string OrganizationId,
    string ProjectId,
    string DatabaseId,
    string State,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long SizeBytes,
    string? Order,
    string? Region)
{
    public TenantContext Storage(PlanTier plan) => new(OrganizationId, plan, ProjectId, Id);

    public DatabaseBackup ToBackup() => new(Id, DatabaseId, "ready", Trigger, SizeBytes, CompletedAt ?? StartedAt);
}

internal sealed record BackupSchedule(DateTimeOffset? LastSucceededAt, DateTimeOffset? LastFailedAt, string? LastErrorCode, string? LastErrorMessage);

internal sealed record BackupLease(string BackupId, string OperationId, DateTimeOffset Until);

internal sealed record RestoreGenerationLease(string DatabaseId, Ulid Generation, string OperationId, DateTimeOffset Until);

internal static class BackupCatalog
{
    public static string Records(string organizationId) => $"organizations/{organizationId}/backups";

    public static string List(string organizationId, string databaseId) => $"organizations/{organizationId}/databases/{databaseId}/backups";

    public static string Retention(string organizationId, string databaseId) => $"organizations/{organizationId}/databases/{databaseId}/backup_retention";

    public static string Policy(string databaseId) => SystemCatalog.DatabaseState(databaseId) + "backup_policy";

    public static string Schedule(string databaseId) => SystemCatalog.DatabaseState(databaseId) + "backup_schedule";

    public static string Leases(string organizationId) => $"organizations/{organizationId}/backup_leases";

    public static string LeaseKey(string backupId, string operationId) => $"{backupId}/{operationId}";

    public static string GenerationLeases(string organizationId) => $"organizations/{organizationId}/restore_generation_leases";

    public static string GenerationLeaseKey(string databaseId, Ulid generation, string operationId) => $"{databaseId}/{generation}/{operationId}";
}
