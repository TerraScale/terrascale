namespace TerraScale.Dashboard;

/// <summary>A side-by-side restore job: creates a new logical database
/// from a restore point and preserves the source (RestoreJobView).</summary>
public sealed record BackupRestoreJob(
    string Id,
    string DatabaseId,
    string DatabaseName,
    string TargetRegion,
    string RestoredDatabaseName,
    string State,          // Pending | Running | Succeeded | Failed
    string StartedAt,
    string? FinishedAt,
    string? Error);
