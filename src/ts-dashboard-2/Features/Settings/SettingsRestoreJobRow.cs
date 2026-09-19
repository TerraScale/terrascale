namespace TerraScale.Dashboard2;

/// <summary>A side-by-side restore started from the settings Backups tab
/// (RestoreJobView).</summary>
public sealed record SettingsRestoreJobRow(
    string Id,
    string SourceDatabaseId,
    string RestoredDatabaseName,
    string RestoredDatabaseId,
    string Status,
    string StatusTone,
    string UpdatedLabel);
