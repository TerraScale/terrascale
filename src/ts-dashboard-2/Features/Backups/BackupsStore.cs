using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

// Backup deep-flow state: seeded restore points and restore jobs plus the
// expiring confirmation-token flow that gates side-by-side restores.
public sealed partial class DashboardStore
{
    public static readonly TimeSpan RestoreConfirmationTtl = TimeSpan.FromMinutes(5);

    private ImmutableArray<BackupRestorePoint> _restorePoints =
    [
        new("gen_01JCB1A3C5E7G9J1K3M5P7R9T", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-east-1",
            "Full", true, 1284, 41_940_812L, "2026-09-18T02:04:00Z"),
        new("gen_01JCB2B4D6F8H0K2L4N6Q8SAV", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-east-1",
            "Incremental", true, 96, 3_145_728L, "2026-09-18T08:02:00Z"),
        new("gen_01JCB3C5E7G9I1K3M5O7S9UBW", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-west-2",
            "Snapshot", true, 1284, 41_940_812L, "2026-09-17T02:03:00Z"),
        new("gen_01JCB4D6F8H0J2L4N6P8TAVCX", "db_01JC9R3Z6P8T0V4X6Z8B2D4GE", "core-sessions", "us-east-1",
            "Full", false, 512, 18_874_368L, "2026-09-18T02:07:00Z"),
        new("gen_01JCB5E7G9I1K3M5O7Q9UAWDY", "db_01JC9S5A7R9V1X3Z5B7D9F3HI", "edge-events", "eu-central-1",
            "Incremental", true, 240, 7_340_032L, "2026-09-18T06:02:00Z"),
    ];

    private ImmutableArray<BackupRestoreJob> _restoreJobs =
    [
        new("res_01JCB6F8H0J2L4N6P8R0VAXEZ", "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", "core-primary", "us-east-1",
            "core-primary-restored-0904", "Succeeded", "2026-09-10T02:41:00Z", "2026-09-10T02:48:00Z", null),
        new("res_01JCB7G9I1K3M5O7Q9S1WBYFA", "db_01JC9R3Z6P8T0V4X6Z8B2D4GE", "core-sessions", "us-east-1",
            "core-sessions-restored-0831", "Failed", "2026-09-06T08:22:00Z", "2026-09-06T08:25:00Z",
            "Incremental chain incomplete: missing base generation for cutoff 2026-09-05T02:07:00Z."),
    ];

    private ImmutableArray<RestorePlan> _restorePlans = [];

    private string _backupsLoadedAt = "2026-09-18T09:12:00Z";

    public ImmutableArray<BackupRestorePoint> RestorePoints => _restorePoints;
    public ImmutableArray<BackupRestoreJob> RestoreJobs => _restoreJobs;
    public string BackupsLoadedAt => _backupsLoadedAt;

    private ImmutableArray<BackupJob> _backupJobs =
    [
        new("bkj_01JCC1A3C5E7G9J1K3M5P7R9T", "core-primary", "Daily 03:00 UTC", "Succeeded", "2026-09-19T03:04:00Z"),
        new("bkj_01JCC2B4D6F8H0K2L4N6Q8SAV", "core-sessions", "Daily 03:00 UTC", "Succeeded", "2026-09-19T03:07:00Z"),
        new("bkj_01JCC3C5E7G9I1K3M5O7S9UBW", "core-primary", "On-demand", "Succeeded", "2026-09-18T21:12:00Z"),
    ];

    public ImmutableArray<BackupJob> BackupJobs => _backupJobs;

    /// <summary>On-demand backup action: records the job and an incremental
    /// restore point for the database, the way the worker would.</summary>
    public string? StartOnDemandBackup(string databaseId)
    {
        if (FindDatabase(databaseId) is not { } database)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        _backupJobs = _backupJobs.Add(new($"bkj_{Guid.NewGuid():N}"[..24], database.Name, "On-demand", "Succeeded", now.ToString("O")));
        _restorePoints = _restorePoints.Add(new(
            $"gen_{Guid.NewGuid():N}"[..24], database.Id, database.Name, database.PrimaryRegion,
            "Incremental", true, 96, 512 * 1024 * 1024L, now.ToString("O")));
        return database.Name;
    }

    /// <summary>Summary tile: the most recent restore job by start time.</summary>
    public BackupRestoreJob? LatestRestore =>
        _restoreJobs.OrderByDescending(job => job.StartedAt, StringComparer.Ordinal).FirstOrDefault();

    public BackupRestorePoint? FindRestorePoint(string generation) =>
        _restorePoints.FirstOrDefault(point => point.Generation == generation);

    /// <summary>Refresh backups action: re-reads restore points and stamps
    /// the loaded-at marker the page renders next to the refresh button.</summary>
    public void RefreshRestorePoints()
    {
        _backupsLoadedAt = DateTimeOffset.UtcNow.ToString("O");
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
        _restorePlans = [plan];
        return plan;
    }

    public RestorePlan? FindRestorePlan(string token) =>
        _restorePlans.FirstOrDefault(plan => plan.Token == token);

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
            _restorePlans = [.. _restorePlans.Where(p => p.Token != token)];
            return "This restore confirmation expired. Review the restore again to create a new plan.";
        }

        if (FindRestorePoint(plan.Generation) is not { } point)
        {
            return "This restore point is no longer available. Refresh backups and review the restore again.";
        }

        _restorePlans = [.. _restorePlans.Where(p => p.Token != token)];
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
        _restoreJobs = _restoreJobs.Add(job);
        _backupsLoadedAt = now.ToString("O");
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
