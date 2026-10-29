namespace TerraScale.DotNet.TerraBase;

/// <summary>A registered, completed snapshot.</summary>
/// <param name="Id">Backup ULID.</param>
/// <param name="DatabaseId">Source database ULID.</param>
/// <param name="Status">ready.</param>
/// <param name="Trigger">manual or scheduled.</param>
/// <param name="SizeBytes">Snapshot bytes.</param>
/// <param name="CreatedAt">Completion time.</param>
public sealed record TerraBaseBackup(string Id, string DatabaseId, string Status, string Trigger, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>Snapshot scheduling and retention policy.</summary>
/// <param name="FullBackupCron">UTC cron, or null for no schedule.</param>
/// <param name="RetentionDays">Retention in days, 1–35.</param>
/// <param name="Disabled">Whether scheduling is paused.</param>
/// <param name="UpdatedAt">Last save, or null for the default policy.</param>
public sealed record TerraBaseBackupPolicy(string? FullBackupCron, int RetentionDays, bool Disabled, DateTimeOffset? UpdatedAt);

/// <summary>The inclusive continuous-backup restore window.</summary>
/// <param name="Earliest">Oldest retained snapshot.</param>
/// <param name="Latest">Newest time proven by stored backup metadata.</param>
public sealed record TerraBaseRestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

/// <summary>Advisory restore metadata; an actual restore also verifies object contents.</summary>
/// <param name="Window">Restore window.</param>
/// <param name="PointInTime">Requested time.</param>
/// <param name="SnapshotAt">Selected snapshot time.</param>
/// <param name="LatestSegmentAt">Latest applicable segment, or null.</param>
/// <param name="SegmentCount">Applicable segments.</param>
/// <param name="HistoryCheck">metadata; not proof of object integrity.</param>
public sealed record TerraBasePointInTimePlan(TerraBaseRestoreWindow Window, DateTimeOffset PointInTime,
    DateTimeOffset SnapshotAt, DateTimeOffset? LatestSegmentAt, int SegmentCount, string HistoryCheck);

/// <summary>A restore creates a new database. This response lacks the serving Region field of lifecycle responses.</summary>
/// <param name="Id">New database ULID.</param>
/// <param name="ProjectId">Project ULID.</param>
/// <param name="Name">New database name.</param>
/// <param name="BranchedFrom">Source database ULID.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="HomeRegion">Placement home, or null.</param>
/// <param name="ReplicaRegions">Placement replicas, or null.</param>
/// <param name="PlacementVersion">Placement revision.</param>
public sealed record TerraBaseRestoredDatabase(string Id, string ProjectId, string Name, string? BranchedFrom,
    DateTimeOffset CreatedAt, string? HomeRegion = null, IReadOnlyList<string>? ReplicaRegions = null, long PlacementVersion = 0)
{
    /// <summary>The replicas, empty when no replica placement was recorded.</summary>
    public IReadOnlyList<string> Replicas => ReplicaRegions ?? [];
}

internal sealed record TerraBaseBackupPolicyRequest(string? FullBackupCron, int RetentionDays, bool Disabled);
internal sealed record TerraBasePointInTimeRequest(DateTimeOffset PointInTime, string? Name, string? Destination);

public sealed partial class TerraBaseDatabase
{
    /// <summary>Creates a completed snapshot. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseBackup> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/backups", null, Json.TerraBaseBackup, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Lists snapshots oldest first. Limit 1–100, default 20; pass opaque Next as after. Requires databases:read.</summary>
    public async Task<TerraBasePage<TerraBaseBackup>> ListBackupsAsync(string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/backups" + TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit))),
            null, Json.TerraBasePageTerraBaseBackup, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Restores a snapshot into a new database; an omitted name selects a free source-restore name. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseRestoredDatabase> RestoreBackupAsync(string backupId, string? name = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/backups/" + TerraScaleIds.Validate(backupId) + "/restore",
            TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody), Json.TerraBaseRestoredDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads the snapshot policy, including the default when unsaved. Requires databases:read.</summary>
    public async Task<TerraBaseBackupPolicy> GetBackupPolicyAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/backup-policy", null, Json.TerraBaseBackupPolicy, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Replaces the scheduling policy; null cron clears the schedule. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseBackupPolicy> SetBackupPolicyAsync(string? fullBackupCron, int retentionDays, bool disabled = false, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path + "/backup-policy",
            TerraBaseConnection.Json(new TerraBaseBackupPolicyRequest(fullBackupCron, retentionDays, disabled), Json.TerraBaseBackupPolicyRequest),
            Json.TerraBaseBackupPolicy, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads the restore window from platform (default) or customer backup. Requires databases:read.</summary>
    public async Task<TerraBaseRestoreWindow> GetRestoreWindowAsync(string? destination = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/point-in-time" + TerraBaseConnection.Query(("destination", destination)),
            null, Json.TerraBaseRestoreWindow, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Previews restore metadata without reserving or proving objects. Requires databases:read.</summary>
    public async Task<TerraBasePointInTimePlan> PlanPointInTimeAsync(DateTimeOffset pointInTime, string? destination = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/point-in-time/plan",
            TerraBaseConnection.Json(new TerraBasePointInTimeRequest(pointInTime, null, destination), Json.TerraBasePointInTimeRequest),
            Json.TerraBasePointInTimePlan, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Restores platform (default) or customer backup into a new database. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseRestoredDatabase> RestorePointInTimeAsync(DateTimeOffset pointInTime, string? name = null, string? destination = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/point-in-time/restore",
            TerraBaseConnection.Json(new TerraBasePointInTimeRequest(pointInTime, name, destination), Json.TerraBasePointInTimeRequest),
            Json.TerraBaseRestoredDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
}
