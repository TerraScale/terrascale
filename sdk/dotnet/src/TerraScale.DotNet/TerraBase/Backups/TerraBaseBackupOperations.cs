using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseBackup(string Id, string DatabaseId, string Status, string Trigger, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record TerraBaseBackupPolicy(string? FullBackupCron, int RetentionDays, DateTimeOffset? UpdatedAt)
{
    [JsonConstructor]
    internal TerraBaseBackupPolicy(int RetentionDays, string? FullBackupCron = null, DateTimeOffset? UpdatedAt = null)
        : this(FullBackupCron, RetentionDays, UpdatedAt)
    {
    }
}

public sealed record TerraBaseRestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

public sealed record TerraBasePointInTimePlan(TerraBaseRestoreWindow Window, DateTimeOffset PointInTime,
    DateTimeOffset SnapshotAt, DateTimeOffset? LatestSegmentAt, int SegmentCount, string HistoryCheck)
{
    [JsonConstructor]
    internal TerraBasePointInTimePlan(TerraBaseRestoreWindow Window, DateTimeOffset PointInTime, DateTimeOffset SnapshotAt,
        int SegmentCount, string HistoryCheck, DateTimeOffset? LatestSegmentAt = null)
        : this(Window, PointInTime, SnapshotAt, LatestSegmentAt, SegmentCount, HistoryCheck)
    {
    }
}

public sealed record TerraBaseRestoredDatabase(string Id, string ProjectId, string Name, string? BranchedFrom,
    DateTimeOffset CreatedAt)
{
    [JsonConstructor]
    internal TerraBaseRestoredDatabase(string Id, string ProjectId, string Name, DateTimeOffset CreatedAt,
        string? BranchedFrom = null)
        : this(Id, ProjectId, Name, BranchedFrom, CreatedAt)
    {
    }
}

public sealed record TerraBaseRestoreOperation(string Id, string OperationId, string Status, string Kind, string Database,
    string? ErrorCode = null, string? ErrorMessage = null, DateTimeOffset CreatedAt = default, DateTimeOffset? FinishedAt = null);

internal static class TerraBaseRestore
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    internal static async Task<T> CompleteAsync<T>(TerraBaseConnection connection, string databasePath, string projectPath,
        TerraBaseRestoreOperation operation, JsonTypeInfo<T> database, CancellationToken cancellationToken)
    {
        var time = connection.TimeProvider;
        var start = time.GetTimestamp();
        while (operation.Status == "running")
        {
            if (time.GetElapsedTime(start) >= Deadline)
                throw new TerraBaseException(HttpStatusCode.GatewayTimeout, "The restore operation did not finish within ten minutes.", "restore_timeout", null);
            await Task.Delay(Interval, time, cancellationToken).ConfigureAwait(false);
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
    public async Task<TerraBaseBackup> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/backups", null, Json.TerraBaseBackup, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBasePage<TerraBaseBackup>> ListBackupsAsync(string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/backups" + TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit))),
            null, Json.TerraBasePageTerraBaseBackup, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseRestoredDatabase> RestoreBackupAsync(string backupId, string? name = null, CancellationToken cancellationToken = default)
    {
        var operation = await connection.SendAsync(HttpMethod.Post, path + "/backups/" + TerraScaleIds.Validate(backupId) + "/restore",
            TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody), Json.TerraBaseRestoreOperation, null, cancellationToken).ConfigureAwait(false);
        return await TerraBaseRestore.CompleteAsync(connection, path, projectPath, operation.Value, Json.TerraBaseRestoredDatabase, cancellationToken).ConfigureAwait(false);
    }

    public Task<TerraBaseRestoreOperation> GetRestoreOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        TerraBaseRestore.ReadAsync(connection, path, operationId, cancellationToken);

    public async Task<TerraBaseBackupPolicy> GetBackupPolicyAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/backup-policy", null, Json.TerraBaseBackupPolicy, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseBackupPolicy> SetBackupPolicyAsync(string? fullBackupCron, int retentionDays, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path + "/backup-policy",
            TerraBaseConnection.Json(new TerraBaseBackupPolicyRequest(fullBackupCron, retentionDays), Json.TerraBaseBackupPolicyRequest),
            Json.TerraBaseBackupPolicy, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseRestoreWindow> GetRestoreWindowAsync(string? destination = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/point-in-time" + TerraBaseConnection.Query(("destination", destination)),
            null, Json.TerraBaseRestoreWindow, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBasePointInTimePlan> PlanPointInTimeAsync(DateTimeOffset pointInTime, string? destination = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/point-in-time/plan",
            TerraBaseConnection.Json(new TerraBasePointInTimeRequest(pointInTime, null, destination), Json.TerraBasePointInTimeRequest),
            Json.TerraBasePointInTimePlan, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseRestoredDatabase> RewindPointInTimeAsync(DateTimeOffset pointInTime, string? destination = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/point-in-time/rewind",
            TerraBaseConnection.Json(new TerraBasePointInTimeRequest(pointInTime, null, destination), Json.TerraBasePointInTimeRequest),
            Json.TerraBaseRestoredDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseRestoredDatabase> RestorePointInTimeAsync(DateTimeOffset pointInTime, string? name = null, string? destination = null, CancellationToken cancellationToken = default)
    {
        var operation = await connection.SendAsync(HttpMethod.Post, path + "/point-in-time/restore",
            TerraBaseConnection.Json(new TerraBasePointInTimeRequest(pointInTime, name, destination), Json.TerraBasePointInTimeRequest),
            Json.TerraBaseRestoreOperation, null, cancellationToken).ConfigureAwait(false);
        return await TerraBaseRestore.CompleteAsync(connection, path, projectPath, operation.Value, Json.TerraBaseRestoredDatabase, cancellationToken).ConfigureAwait(false);
    }
}
