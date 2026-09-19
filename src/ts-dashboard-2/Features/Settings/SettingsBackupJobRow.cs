namespace TerraScale.Dashboard2;

/// <summary>One backup run of the selected database (BackupJobView).</summary>
public sealed record SettingsBackupJobRow(
    string Id,
    string Policy,
    string Retention,
    string Status,
    string StatusTone,
    string DestinationType,
    bool HasRestorePoint,
    string UpdatedLabel);
