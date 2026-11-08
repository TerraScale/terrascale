using System.Text.Json.Serialization;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed record BackupView(string Id, string Status, DateTimeOffset CreatedAt, long SizeBytes);
    public sealed record BackupPolicyView(string FullBackupCron, string IncrementalBackupCron, int RetentionDays, bool Disabled,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UpdatedAt = null);
    public sealed record BackupListing(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackupPolicyView? Policy,
        IReadOnlyList<BackupView> Backups)
    {
        [JsonIgnore] public string PolicyUnavailable { get; init; } = "";
    }
    public sealed record BackupRestList(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackupPolicyView? Policy,
        IReadOnlyList<BackupView> Backups,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor,
        bool HasMore,
        int Limit);
    // One bounded producer page: items as the dashboard renders them, plus the producer's own continuation.
    public sealed record BackupPageSlice(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackupPolicyView? Policy,
        IReadOnlyList<BackupView> Items,
        string? NextCursor)
    {
        [JsonIgnore] public string PolicyUnavailable { get; init; } = "";
        public bool HasMore => NextCursor is not null;
    }
    public sealed record BackupRestoreResult(string DatabaseId, string Name, string Status);
    public sealed record BackupRestoreStart(string OperationId, string Status);
    public sealed record ProducerRestoreOperation(string OperationId, string Status, string? Database, string? ErrorCode, string? ErrorMessage);
    public sealed record RestoreStatusResponse(string OperationId, string Status, string? TargetDatabaseId, string? TargetName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage = null);
    public sealed record RestoreProgress(string OperationId, string Status, string? TargetDatabaseId = null, string? TargetName = null,
        string? ErrorCode = null, string? ErrorMessage = null)
    {
        public string Failure => ErrorMessage is { Length: > 0 } message ? message : "The restore could not be completed.";
    }
    public sealed record ProducerBackup(string Id, string DatabaseId, string Status, string Trigger, long SizeBytes, DateTimeOffset CreatedAt);
    public sealed record BackupBackendPage(IReadOnlyList<ProducerBackup> Items, string? NextCursor);
    public sealed record ProducerBackupPolicy(string? FullBackupCron, int RetentionDays, bool Disabled, DateTimeOffset? UpdatedAt);
    // The project's own bucket at TerraBase (credentials are write-only there) and a database's scheduled NDJSON export into it.
    public sealed record BackupDestinationView(string Endpoint, string Region, string Bucket, string Mode, DateTimeOffset? UpdatedAt);
    public sealed record BackupBucketView(BackupDestinationView? Destination);
    public sealed record BackupPage(ShellModel Shell, DatabaseRecord Database, BackupListing? Listing, string Unavailable = "",
        string Error = "", string RestoreId = "", string Name = "", string? FullBackupCron = null, string? RetentionDays = null)
    {
        public BackupBucketView? Bucket { get; init; }
        public bool BucketRead { get; init; }
        public RestoreProgress? Restore { get; init; }
    }

    public interface IBackupBackend
    {
        Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<BackupBackendPage> ListPageAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? after, int limit, CancellationToken cancellationToken);
        Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? fullBackupCron,
            int retentionDays, bool disabled, CancellationToken cancellationToken);
        Task<BackupDestinationView?> DestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
        Task<BackupDestinationView> SetDestinationAsync(DatabaseScope scope, DatabaseRegion home, string endpoint, string region, string bucket,
            string accessKeyId, string secretKey, CancellationToken cancellationToken);
        Task RemoveDestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
        Task<ManagedDatabase> RestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken cancellationToken);
        Task<ProducerRestoreOperation> StartRestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken cancellationToken);
        Task<ProducerRestoreOperation> RestoreOperationAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string operationId, CancellationToken cancellationToken);
        Task<ManagedDatabase> RestoreTargetAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string targetName, CancellationToken cancellationToken);
    }
}
