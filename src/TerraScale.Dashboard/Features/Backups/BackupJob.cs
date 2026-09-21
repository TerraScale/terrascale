namespace TerraScale.Dashboard;

/// <summary>A backup run for one database (policy or on-demand), mirroring
/// the backup jobs table of ts-dashboard's BackupSettingsPanel.</summary>
public sealed record BackupJob(
    string Id,
    string DatabaseName,
    string Policy,         // Daily 03:00 UTC | Hourly | On-demand
    string State,          // Succeeded | Running | Failed
    string UpdatedAt);
