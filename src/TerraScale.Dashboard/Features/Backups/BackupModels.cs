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
    public sealed record BackupRestoreResult(string DatabaseId, string Name, string Status);
    public sealed record ProducerBackup(string Id, string DatabaseId, string Status, string Trigger, long SizeBytes, DateTimeOffset CreatedAt);
    public sealed record ProducerBackupPolicy(string? FullBackupCron, int RetentionDays, bool Disabled, DateTimeOffset? UpdatedAt);
    // The project's own bucket at TerraBase (credentials are write-only there) and a database's scheduled NDJSON export into it.
    public sealed record BackupDestinationView(string Endpoint, string Region, string Bucket, string Mode, DateTimeOffset? UpdatedAt);
    public sealed record ExportRunView(DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, string? Key, long Documents, long Bytes, string? Error);
    public sealed record ExportScheduleView(string? Cron, DateTimeOffset? UpdatedAt, ExportRunView? LastRun);
    public sealed record BackupExportView(BackupDestinationView? Destination, ExportScheduleView Schedule);
    public sealed record BackupPage(ShellModel Shell, DatabaseRecord Database, BackupListing? Listing, string Unavailable = "",
        string Error = "", string RestoreId = "", string Name = "", string? FullBackupCron = null, string? RetentionDays = null)
    {
        public BackupExportView? Export { get; init; }
        public bool ExportRead { get; init; }
    }

    public interface IBackupBackend
    {
        Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? fullBackupCron,
            int retentionDays, bool disabled, CancellationToken cancellationToken);
        Task<BackupDestinationView?> DestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
        Task<BackupDestinationView> SetDestinationAsync(DatabaseScope scope, DatabaseRegion home, string endpoint, string region, string bucket,
            string accessKeyId, string secretKey, CancellationToken cancellationToken);
        Task RemoveDestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
        Task<ExportScheduleView> ExportScheduleAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ExportScheduleView> SetExportScheduleAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? cron, CancellationToken cancellationToken);
        Task<ManagedDatabase> RestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken cancellationToken);
    }
}
