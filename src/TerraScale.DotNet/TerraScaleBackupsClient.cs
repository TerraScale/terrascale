namespace TerraScale.DotNet;

/// <summary>
/// Client for project database backup and restore endpoints.
/// </summary>
public sealed class TerraScaleBackupsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleBackupsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Configures a backup policy for a project database.
    /// </summary>
    public Task<TerraScaleBackupPolicy> ConfigurePolicyAsync(
        string projectId,
        string databaseId,
        TerraScaleConfigureBackupPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FullBackupCron);
        return client.SendAsync<TerraScaleBackupPolicy>(
            HttpMethod.Put,
            TerraScaleApiRoutes.BackupPolicyConfigure(projectId, databaseId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Triggers an on-demand backup for a project database.
    /// </summary>
    public Task<TerraScaleBackupJob> TriggerAsync(
        string projectId,
        string databaseId,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return client.SendAsync<TerraScaleBackupJob>(
            HttpMethod.Post,
            TerraScaleApiRoutes.BackupsCreate(projectId, databaseId),
            new TerraScaleTriggerBackupRequest(idempotencyKey),
            cancellationToken);
    }

    /// <summary>
    /// Lists backup jobs for a project database.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleBackupJob>> ListAsync(
        string projectId,
        string databaseId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleBackupJob>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.BackupsList(projectId, databaseId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets one backup job by id.
    /// </summary>
    public Task<TerraScaleBackupJob> GetAsync(
        string projectId,
        string databaseId,
        string backupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        return client.SendAsync<TerraScaleBackupJob>(
            HttpMethod.Get,
            TerraScaleApiRoutes.BackupGet(projectId, databaseId, backupId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Lists restore points visible for a project database.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleRestorePoint>> ListRestorePointsAsync(
        string projectId,
        string databaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return client.SendAsync<IReadOnlyList<TerraScaleRestorePoint>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.RestorePointsList(projectId, databaseId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Starts a restore from a backup into a logical restored database.
    /// </summary>
    public Task<TerraScaleRestoreJob> StartRestoreAsync(
        string projectId,
        string databaseId,
        string backupId,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        return client.SendAsync<TerraScaleRestoreJob>(
            HttpMethod.Post,
            TerraScaleApiRoutes.RestoresCreate(projectId, databaseId),
            new TerraScaleStartRestoreRequest(backupId.Trim(), idempotencyKey),
            cancellationToken);
    }

    /// <summary>
    /// Lists restore jobs for a project database.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleRestoreJob>> ListRestoresAsync(
        string projectId,
        string databaseId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleRestoreJob>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.RestoresList(projectId, databaseId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets one restore job by id.
    /// </summary>
    public Task<TerraScaleRestoreJob> GetRestoreAsync(
        string projectId,
        string databaseId,
        string restoreId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);
        return client.SendAsync<TerraScaleRestoreJob>(
            HttpMethod.Get,
            TerraScaleApiRoutes.RestoreGet(projectId, databaseId, restoreId),
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// Request body for configuring a project database backup policy.
/// </summary>
public sealed record TerraScaleConfigureBackupPolicyRequest(
    string FullBackupCron,
    string? IncrementalBackupCron = null,
    int RetentionDays = 30,
    bool Disabled = false);

internal sealed record TerraScaleTriggerBackupRequest(string? IdempotencyKey);

internal sealed record TerraScaleStartRestoreRequest(
    string BackupId,
    string? IdempotencyKey);

/// <summary>
/// Backup policy configured for a project database.
/// </summary>
public sealed record TerraScaleBackupPolicy(
    string BackupPolicyId,
    string ProjectId,
    string DatabaseId,
    string DatabaseName,
    string FullBackupCron,
    string? IncrementalBackupCron,
    int RetentionDays,
    bool Disabled,
    string DestinationType,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Backup job returned by TerraScale.
/// </summary>
public sealed record TerraScaleBackupJob(
    string BackupId,
    string ProjectId,
    string DatabaseId,
    string DatabaseName,
    string Policy,
    string Retention,
    string Status,
    string DestinationType,
    bool HasRestorePoint,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);

/// <summary>
/// Restore point returned by TerraScale.
/// </summary>
public sealed record TerraScaleRestorePoint(
    string Name,
    string? BackupId,
    DateTimeOffset CreatedAt,
    bool IsSnapshotRestore,
    bool IsEncrypted,
    bool IsIncremental,
    int FilesToRestore);

/// <summary>
/// Restore job returned by TerraScale.
/// </summary>
public sealed record TerraScaleRestoreJob(
    string RestoreId,
    string ProjectId,
    string DatabaseId,
    string BackupId,
    string RestoredDatabaseId,
    string RestoredDatabaseName,
    string Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);
