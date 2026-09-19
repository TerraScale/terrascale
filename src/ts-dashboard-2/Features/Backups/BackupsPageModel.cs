using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Backups;

public sealed record BackupsPageModel(
    string Project,
    ImmutableArray<RestorePointRow> RestorePoints,
    ImmutableArray<RestoreJobRow> RestoreJobs,
    RestoreJobRow? LatestRestore,
    RestoreConfirmationView? Confirmation,
    string LoadedAtLabel,
    string? RestoredNotice,
    string? Error) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static BackupsPageModel From(DashboardStore store, string? planToken, string? restoredJobId, string? error)
    {
        var restoredJob = restoredJobId is null
            ? null
            : store.RestoreJobs.FirstOrDefault(job => job.Id == restoredJobId);

        return new(
            Project: ShortId(store.ActiveProjectId),
            RestorePoints: [.. store.RestorePoints.Select(ToPointRow)],
            RestoreJobs: [.. store.RestoreJobs.Select(ToJobRow)],
            LatestRestore: store.LatestRestore is { } latest ? ToJobRow(latest) : null,
            Confirmation: planToken is null ? null : ToConfirmation(store, planToken),
            LoadedAtLabel: DashboardStore.FormatUtcDateTime(store.BackupsLoadedAt),
            RestoredNotice: restoredJob?.RestoredDatabaseName,
            Error: error)
        {
            Shell = ShellFactory.Create(store, "/backups"),
        };
    }

    private static RestorePointRow ToPointRow(BackupRestorePoint point) => new(
        GenerationId: point.Generation,
        Generation: ShortId(point.Generation),
        Database: point.DatabaseName,
        Region: point.Region,
        Mode: point.Mode,
        ModeTone: point.Mode == "Incremental" ? "ts-badge-info" : "ts-badge-success",
        EncryptionLabel: point.Encrypted ? "Encrypted" : "Not encrypted",
        FilesToRestore: point.FilesToRestore,
        SizeLabel: DashboardStore.FormatBytes(point.SizeBytes),
        CreatedLabel: DashboardStore.FormatUtcDateTime(point.CreatedAt));

    private static RestoreJobRow ToJobRow(BackupRestoreJob job) => new(
        Id: ShortId(job.Id),
        Database: job.DatabaseName,
        TargetRegion: job.TargetRegion,
        RestoredDatabaseName: job.RestoredDatabaseName,
        State: job.State,
        StateTone: DashboardStore.BackupStatusTone(job.State),
        StartedLabel: DashboardStore.FormatUtcDateTime(job.StartedAt),
        FinishedLabel: job.FinishedAt is { } finished ? DashboardStore.FormatUtcDateTime(finished) : "-",
        Error: job.Error);

    private static RestoreConfirmationView? ToConfirmation(DashboardStore store, string token)
    {
        if (store.FindRestorePlan(token) is not { } plan || store.FindRestorePoint(plan.Generation) is not { } point)
        {
            return null;
        }

        var expires = DateTimeOffset.TryParse(plan.ExpiresAt, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        var remaining = expires - DateTimeOffset.UtcNow;
        return new RestoreConfirmationView(
            Token: plan.Token,
            DatabaseName: plan.DatabaseName,
            Generation: ShortId(plan.Generation),
            Region: plan.TargetRegion,
            Action: "Restore side-by-side",
            CreatedLabel: DashboardStore.FormatUtcDateTime(plan.CreatedAt),
            ExpiresLabel: DashboardStore.FormatUtcDateTime(plan.ExpiresAt),
            TtlLabel: remaining > TimeSpan.Zero
                ? $"Expires in {(int)remaining.TotalMinutes}m {remaining.Seconds:D2}s"
                : "Expired",
            CutoffWarning:
                $"Data written to {plan.DatabaseName} after {DashboardStore.FormatUtcDateTime(point.CreatedAt)} (the restore point cutoff) is not included in the restored database.");
    }

    /// <summary>shortId from src/lib/formatting.ts.</summary>
    public static string ShortId(string id) => id.Length <= 12 ? id : $"{id[..6]}...{id[^4..]}";
}
