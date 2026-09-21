namespace TerraScale.Dashboard;

/// <summary>Per-database backup policy configured on the settings Backups
/// tab (ConfigureBackupPolicyRequest in backups-data.ts).</summary>
public sealed record SettingsBackupPolicy(
    string DatabaseId,
    string FullBackupCron,
    string IncrementalBackupCron,
    int RetentionDays,
    bool Disabled);
