namespace TerraScale.Dashboard2.Features.Backups;

public sealed record RestoreJobRow(
    string Id,
    string Database,
    string TargetRegion,
    string RestoredDatabaseName,
    string State,
    string StateTone,
    string StartedLabel,
    string FinishedLabel,
    string? Error);
