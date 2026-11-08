using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Backups;

/// <summary>
/// The states of a backup. A backup moves only forward:
/// <c>pending</c> → <c>snapshotting</c> → <c>registered</c> → <c>expiring</c> → deleted, or from <c>pending</c> or
/// <c>snapshotting</c> straight to <c>expiring</c> when its snapshot fails or is abandoned. Deleted is terminal: the
/// record is removed once its files are confirmed gone.
/// </summary>
internal static class BackupState
{
    /// <summary>Recorded, not started. No file exists yet.</summary>
    public const string Pending = "pending";

    /// <summary>The snapshot copy is running; its file may exist.</summary>
    public const string Snapshotting = "snapshotting";

    /// <summary>Complete and listed; the only state a backup can be restored from.</summary>
    public const string Registered = "registered";

    /// <summary>Expired, failed or abandoned: no longer listed, its files are deleted by the sweep.</summary>
    public const string Expiring = "expiring";
}

/// <summary>A backup and where it is in its lifecycle.</summary>
/// <param name="Id">Canonical uppercase ULID; also the id of the snapshot, an engine database of the backed-up database's project (<see cref="Storage"/>).</param>
/// <param name="OrganizationId">The organization (tenant) of the backed-up database.</param>
/// <param name="ProjectId">Its project.</param>
/// <param name="DatabaseId">The backed-up database.</param>
/// <param name="State">One of <see cref="BackupState"/>.</param>
/// <param name="Trigger"><c>manual</c> or <c>scheduled</c>.</param>
/// <param name="StartedAt">When it was recorded.</param>
/// <param name="CompletedAt">When it was registered; retention counts from here.</param>
/// <param name="SizeBytes">Bytes the snapshot occupies, once registered.</param>
/// <param name="Order">Its key in the database's backup list, once registered.</param>
/// <param name="Region">The region holding the snapshot file; absent records cannot be routed for restore.</param>
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
    /// <summary>
    /// The engine database that holds the snapshot: in the organization's directory, next to the backed-up database in its
    /// project. No catalog entry names it, so the data API cannot reach it. <paramref name="plan"/> is the organization's.
    /// </summary>
    public TenantContext Storage(PlanTier plan) => new(OrganizationId, plan, ProjectId, Id);

    /// <summary>The listed form; only for <see cref="BackupState.Registered"/> backups.</summary>
    public DatabaseBackup ToBackup() => new(Id, DatabaseId, "ready", Trigger, SizeBytes, CompletedAt ?? StartedAt);
}

/// <summary>The scheduled backup history of a database, kept even after retention deletes its backups.</summary>
/// <param name="LastSucceededAt">When the last scheduled backup was registered, if ever; the cron counts from here.</param>
/// <param name="LastFailedAt">When the last scheduled attempt failed, if it failed after the last success.</param>
/// <param name="LastErrorCode">A stable code for the failed attempt, when one is known; null when its cause is unknown.</param>
/// <param name="LastErrorMessage">A safe description of the failed attempt; never internal paths or secrets.</param>
internal sealed record BackupSchedule(DateTimeOffset? LastSucceededAt, DateTimeOffset? LastFailedAt, string? LastErrorCode, string? LastErrorMessage);

/// <summary>
/// A running restore's hold on a backup snapshot: written in the transaction that admits the restore, renewed while its
/// copy runs and deleted when it settles; its expiry bounds a lease whose holder never comes back. One backup can hold
/// several leases at once, one per restoring operation.
/// </summary>
/// <param name="BackupId">The leased backup.</param>
/// <param name="OperationId">The restore operation whose copy still reads the snapshot.</param>
/// <param name="Until">When the lease expires unless renewed.</param>
internal sealed record BackupLease(string BackupId, string OperationId, DateTimeOffset Until);

internal sealed record RestoreGenerationLease(string DatabaseId, Ulid Generation, string OperationId, DateTimeOffset Until);

/// <summary>
/// Where the lifecycle lives in the catalog. Backups are kept under their organization, not under the database, so
/// deleting a database never loses track of a snapshot file: its backups are expired and swept like any other.
/// </summary>
internal static class BackupCatalog
{
    /// <summary>Every backup of an organization, keyed by backup id.</summary>
    public static string Records(string organizationId) => $"organizations/{organizationId}/backups";

    /// <summary>The registered backups of a database, keyed by <see cref="BackupRecord.Order"/> (oldest first).</summary>
    public static string List(string organizationId, string databaseId) => $"organizations/{organizationId}/databases/{databaseId}/backups";

    /// <summary>
    /// The same backups for retention, keyed by <see cref="BackupRecord.CompletedAt"/> ticks then <see cref="BackupRecord.Order"/>
    /// (oldest completion first) → backup id. Written and deleted with the list entry.
    /// </summary>
    public static string Retention(string organizationId, string databaseId) => $"organizations/{organizationId}/databases/{databaseId}/backup_retention";

    /// <summary>The policy of a database, deleted with it.</summary>
    public static string Policy(string databaseId) => SystemCatalog.DatabaseState(databaseId) + "backup_policy";

    /// <summary>The <see cref="BackupSchedule"/> of a database, deleted with it.</summary>
    public static string Schedule(string databaseId) => SystemCatalog.DatabaseState(databaseId) + "backup_schedule";

    /// <summary>The <see cref="BackupLease"/> records of an organization, keyed by backup id then operation id.</summary>
    public static string Leases(string organizationId) => $"organizations/{organizationId}/backup_leases";

    /// <summary>The key of one operation's lease on one backup.</summary>
    public static string LeaseKey(string backupId, string operationId) => $"{backupId}/{operationId}";

    /// <summary>The <see cref="RestoreGenerationLease"/> records of an organization, keyed by database id, generation, then operation id.</summary>
    public static string GenerationLeases(string organizationId) => $"organizations/{organizationId}/restore_generation_leases";

    /// <summary>The key of one operation's lease on one database generation.</summary>
    public static string GenerationLeaseKey(string databaseId, Ulid generation, string operationId) => $"{databaseId}/{generation}/{operationId}";
}
