using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
/// <param name="UpdatedAt">Last save, or null for the default policy.</param>
public sealed record TerraBaseBackupPolicy(string? FullBackupCron, int RetentionDays, DateTimeOffset? UpdatedAt);

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
/// <param name="HomeRegion">The region holding the new database, or null.</param>
public sealed record TerraBaseRestoredDatabase(string Id, string ProjectId, string Name, string? BranchedFrom,
    DateTimeOffset CreatedAt, string? HomeRegion = null);

/// <summary>A background restore started by a restore route; read it with <see cref="TerraBaseDatabase.GetRestoreOperationAsync"/>.</summary>
/// <param name="Id">Operation ULID.</param>
/// <param name="OperationId">Operation ULID, identical to <paramref name="Id"/>.</param>
/// <param name="Status">running, completed or failed.</param>
/// <param name="Kind">backup or point-in-time.</param>
/// <param name="Database">Name of the source (running) or restored (completed) database.</param>
/// <param name="ErrorCode">Failure code when <paramref name="Status"/> is failed.</param>
/// <param name="ErrorMessage">Failure explanation when <paramref name="Status"/> is failed.</param>
/// <param name="CreatedAt">Start time.</param>
/// <param name="FinishedAt">Terminal time, or null while running.</param>
public sealed record TerraBaseRestoreOperation(string Id, string OperationId, string Status, string Kind, string Database,
    string? ErrorCode = null, string? ErrorMessage = null, DateTimeOffset CreatedAt = default, DateTimeOffset? FinishedAt = null);

internal static class TerraBaseRestore
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    internal static async Task<T> CompleteAsync<T>(TerraBaseConnection connection, string databasePath, string projectPath,
        TerraBaseRestoreOperation operation, JsonTypeInfo<T> database, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Deadline);
        while (operation.Status == "running")
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TerraBaseException(HttpStatusCode.GatewayTimeout, "The restore operation did not finish within ten minutes.", "restore_timeout", null);
            await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            operation = await ReadAsync(connection, databasePath, operation.Id, cancellationToken).ConfigureAwait(false);
        }
        if (operation.Status != "completed")
            throw new TerraBaseException(HttpStatusCode.OK, operation.ErrorMessage ?? "The restore operation failed.", operation.ErrorCode ?? "restore_failed", null);
        return (await connection.SendAsync(HttpMethod.Get,
            projectPath + "/databases/" + TerraBaseConnection.Segment(operation.Database, "database"), null, database, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    internal static async Task<TerraBaseRestoreOperation> ReadAsync(TerraBaseConnection connection, string databasePath,
        string operationId, CancellationToken cancellationToken) =>
        (await connection.SendAsync(HttpMethod.Get, databasePath + "/restores/" + TerraScaleIds.Validate(operationId, nameof(operationId)),
            null, TerraBaseJsonContext.Wire.TerraBaseRestoreOperation, null, cancellationToken).ConfigureAwait(false)).Value;
}

internal sealed record TerraBaseBackupPolicyRequest(string? FullBackupCron, int RetentionDays);
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

    /// <summary>Starts restoring a snapshot into a new database, polls the operation to its ten-minute deadline and returns the restored database; an omitted name selects a free source-restore name. Requires databases:write for the restore and databases:read for the poll; sent once.</summary>
    public async Task<TerraBaseRestoredDatabase> RestoreBackupAsync(string backupId, string? name = null, CancellationToken cancellationToken = default)
    {
        var operation = await connection.SendAsync(HttpMethod.Post, path + "/backups/" + TerraScaleIds.Validate(backupId) + "/restore",
            TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody), Json.TerraBaseRestoreOperation, null, cancellationToken).ConfigureAwait(false);
        return await TerraBaseRestore.CompleteAsync(connection, path, projectPath, operation.Value, Json.TerraBaseRestoredDatabase, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a background restore operation of this database. Requires databases:read.</summary>
    public Task<TerraBaseRestoreOperation> GetRestoreOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        TerraBaseRestore.ReadAsync(connection, path, operationId, cancellationToken);

    /// <summary>Reads the snapshot policy, including the default when unsaved. Requires databases:read.</summary>
    public async Task<TerraBaseBackupPolicy> GetBackupPolicyAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/backup-policy", null, Json.TerraBaseBackupPolicy, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Replaces the scheduling policy; null cron clears the schedule. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseBackupPolicy> SetBackupPolicyAsync(string? fullBackupCron, int retentionDays, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path + "/backup-policy",
            TerraBaseConnection.Json(new TerraBaseBackupPolicyRequest(fullBackupCron, retentionDays), Json.TerraBaseBackupPolicyRequest),
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

    /// <summary>Rewinds this database in place to the selected instant from platform (default) or customer backup, discarding later changes in one transaction; the rewind itself is backed up. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseRestoredDatabase> RewindPointInTimeAsync(DateTimeOffset pointInTime, string? destination = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/point-in-time/rewind",
            TerraBaseConnection.Json(new TerraBasePointInTimeRequest(pointInTime, null, destination), Json.TerraBasePointInTimeRequest),
            Json.TerraBaseRestoredDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Starts restoring platform (default) or customer backup into a new database, polls the operation to its ten-minute deadline and returns the restored database. Requires databases:write for the restore and databases:read for the poll; sent once.</summary>
    public async Task<TerraBaseRestoredDatabase> RestorePointInTimeAsync(DateTimeOffset pointInTime, string? name = null, string? destination = null, CancellationToken cancellationToken = default)
    {
        var operation = await connection.SendAsync(HttpMethod.Post, path + "/point-in-time/restore",
            TerraBaseConnection.Json(new TerraBasePointInTimeRequest(pointInTime, name, destination), Json.TerraBasePointInTimeRequest),
            Json.TerraBaseRestoreOperation, null, cancellationToken).ConfigureAwait(false);
        return await TerraBaseRestore.CompleteAsync(connection, path, projectPath, operation.Value, Json.TerraBaseRestoredDatabase, cancellationToken).ConfigureAwait(false);
    }
}
