using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Backups;

public sealed record BackupView(string Id, string Status, DateTimeOffset CreatedAt, long SizeBytes);
public sealed record BackupPolicyView(string FullBackupCron, string IncrementalBackupCron, int RetentionDays, bool Disabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UpdatedAt = null);
public sealed record BackupListing(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackupPolicyView? Policy,
    IReadOnlyList<BackupView> Backups)
{
    [JsonIgnore] public string PolicyUnavailable { get; init; } = "";
}
public sealed record BackupRestoreResult(string DatabaseId, string Name, string Status);
public sealed record ProducerBackup(string Id, string DatabaseId, string Status, string Trigger, long SizeBytes, DateTimeOffset CreatedAt);
public sealed record ProducerBackupPolicy(string? FullBackupCron, int RetentionDays, bool Disabled, DateTimeOffset? UpdatedAt);
public sealed record BackupPage(ShellModel Shell, DatabaseRecord Database, BackupListing? Listing, string Unavailable = "",
    string Error = "", string RestoreId = "", string Name = "", string? FullBackupCron = null, string? RetentionDays = null);

public interface IBackupBackend
{
    Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? fullBackupCron,
        int retentionDays, bool disabled, CancellationToken cancellationToken);
    Task<ManagedDatabase> RestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken cancellationToken);
}
