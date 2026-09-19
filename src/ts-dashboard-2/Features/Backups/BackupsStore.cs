using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

// Backup deep-flow state: restore points and restore jobs plus the
// expiring confirmation-token flow that gates side-by-side restores.
// State lives on the active account world.
public sealed partial class DashboardStore
{

    /// <summary>v1 shortId: first 6 + last 4 characters of long ids.</summary>
    public static string ShortId(string id) => id.Length <= 12 ? id : $"{id[..6]}...{id[^4..]}";
    public static readonly TimeSpan RestoreConfirmationTtl = TimeSpan.FromMinutes(5);

    public ImmutableArray<BackupRestorePoint> RestorePoints { get => World.RestorePoints; private set => World.RestorePoints = value; }
    public ImmutableArray<BackupRestoreJob> RestoreJobs { get => World.RestoreJobs; private set => World.RestoreJobs = value; }
    public ImmutableArray<RestorePlan> RestorePlans { get => World.RestorePlans; private set => World.RestorePlans = value; }
    public string BackupsLoadedAt { get => World.BackupsLoadedAt; private set => World.BackupsLoadedAt = value; }
    public ImmutableArray<BackupJob> BackupJobs { get => World.BackupJobs; private set => World.BackupJobs = value; }

    /// <summary>On-demand backup action: records the job and an incremental
    /// restore point for the database, the way the worker would.</summary>
    public string? StartOnDemandBackup(string databaseId)
    {
        if (FindDatabase(databaseId) is not { } database)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        BackupJobs = BackupJobs.Add(new($"bkj_{Guid.NewGuid():N}"[..24], database.Name, "On-demand", "Succeeded", now.ToString("O")));
        RestorePoints = RestorePoints.Add(new(
            $"gen_{Guid.NewGuid():N}"[..24], database.Id, database.Name, database.PrimaryRegion,
            "Incremental", true, 96, 512 * 1024 * 1024L, now.ToString("O")));
        return database.Name;
    }

    /// <summary>Summary tile: the most recent restore job by start time.</summary>
    public BackupRestoreJob? LatestRestore =>
        RestoreJobs.OrderByDescending(job => job.StartedAt, StringComparer.Ordinal).FirstOrDefault();

    public BackupRestorePoint? FindRestorePoint(string generation) =>
        RestorePoints.FirstOrDefault(point => point.Generation == generation);

    /// <summary>Refresh backups action: re-reads restore points and stamps
    /// the loaded-at marker the page renders next to the refresh button.</summary>
    public void RefreshRestorePoints()
    {
        BackupsLoadedAt = DateTimeOffset.UtcNow.ToString("O");
    }

    /// <summary>Plan step of the plan -> confirm flow: nothing runs yet, the
    /// returned plan just carries an expiring confirmation token.</summary>
    public RestorePlan? PlanRestore(string generation)
    {
        if (FindRestorePoint(generation) is not { } point)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var plan = new RestorePlan(
            Token: $"cfm_{Guid.NewGuid():N}"[..24],
            DatabaseId: point.DatabaseId,
            DatabaseName: point.DatabaseName,
            Generation: point.Generation,
            TargetRegion: point.Region,
            RequestDigest: Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant(),
            CreatedAt: now.ToString("O"),
            ExpiresAt: now.Add(RestoreConfirmationTtl).ToString("O"));

        // A new plan supersedes any stale ones instead of piling up.
        RestorePlans = [plan];
        return plan;
    }

    public RestorePlan? FindRestorePlan(string token) =>
        RestorePlans.FirstOrDefault(plan => plan.Token == token);

    /// <summary>Confirm step: consumes the token (single use) and records the
    /// restore job. Returns an error string, or null when the restore started.</summary>
    public string? ExecuteRestore(string token, out BackupRestoreJob? job)
    {
        job = null;
        var plan = FindRestorePlan(token);
        if (plan is null)
        {
            return "This restore confirmation is no longer valid. Review the restore again to create a new plan.";
        }

        if (DateTimeOffset.TryParse(plan.ExpiresAt, out var expires) && expires <= DateTimeOffset.UtcNow)
        {
            RestorePlans = [.. RestorePlans.Where(p => p.Token != token)];
            return "This restore confirmation expired. Review the restore again to create a new plan.";
        }

        if (FindRestorePoint(plan.Generation) is not { } point)
        {
            return "This restore point is no longer available. Refresh backups and review the restore again.";
        }

        RestorePlans = [.. RestorePlans.Where(p => p.Token != token)];
        var now = DateTimeOffset.UtcNow;
        job = new BackupRestoreJob(
            Id: $"res_{Guid.NewGuid():N}"[..20],
            DatabaseId: plan.DatabaseId,
            DatabaseName: plan.DatabaseName,
            TargetRegion: plan.TargetRegion,
            RestoredDatabaseName: $"{plan.DatabaseName}-restored-{now:HHmm}",
            State: "Running",
            StartedAt: now.ToString("O"),
            FinishedAt: null,
            Error: null);
        RestoreJobs = RestoreJobs.Add(job);
        BackupsLoadedAt = now.ToString("O");
        return null;
    }

    /// <summary>Port of statusTone from BackupSettingsPanel.tsx.</summary>
    public static string BackupStatusTone(string status) => status.ToLowerInvariant() switch
    {
        var s when s.Contains("fail") || s.Contains("error") => "ts-badge-danger",
        var s when s.Contains("succeed") || s.Contains("complete") || s.Contains("success") => "ts-badge-success",
        var s when s.Contains("running") || s.Contains("pending") || s.Contains("start") => "ts-badge-info",
        _ => "ts-badge-neutral",
    };
}
