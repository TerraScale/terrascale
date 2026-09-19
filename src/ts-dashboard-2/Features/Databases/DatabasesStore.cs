using System.Collections.Immutable;
using TerraScale.Dashboard2.Features.Databases;

namespace TerraScale.Dashboard2;

public sealed partial class DashboardStore
{
    /// <summary>Maps a stored database to the list/detail view the original
    /// DatabaseResource carries (provisioning-state status + replication label).</summary>
    public DatabaseRow ToRow(DashboardDatabase database)
    {
        var regions = new List<string> { database.PrimaryRegion };
        if (database.BackupRegion is { } backup)
        {
            regions.Add(backup);
        }

        var status = database.Status switch
        {
            "Active" => "ready",
            "Provisioning" => "provisioning",
            _ => database.Status.ToLowerInvariant(),
        };
        return new DatabaseRow(
            Id: database.Id,
            Name: database.Name,
            Regions: string.Join(",", regions),
            BackupRegion: database.BackupRegion,
            Replication: ReplicationLabel(regions.Count),
            Status: status,
            StatusTone: status switch { "ready" => "ts-badge-success", "failed" => "ts-badge-danger", _ => "ts-badge-neutral" },
            Provisioning: database.Status == "Provisioning",
            OperationId: database.Status == "Provisioning" ? $"op_{database.Id[^8..]}" : null,
            CreatedAt: database.CreatedAt,
            Version: int.TryParse(database.Version, out var v) ? v : 1);
    }

    public static string ReplicationLabel(int regionCount) => regionCount switch
    {
        >= 3 => "3-region ring",
        2 => "2-region replica",
        _ => "Single region",
    };

    /// <summary>Inter-region round-trip estimates for the detail page latency table.</summary>
    public ImmutableArray<LatencyRow> LatencyFor(DashboardDatabase database)
    {
        // Fresh projects sit in authority regions (SFO/GRU/FRA) that are not in
        // the placement catalog; the table still renders a primary row.
        var primary = Regions.FirstOrDefault(r => r.Code == database.PrimaryRegion);
        var rows = primary is null
            ? [new LatencyRow(database.PrimaryRegion, "—")]
            : new List<LatencyRow> { new(primary.Code, $"{primary.LatencyMs} ms") };
        if (primary is { } p && database.BackupRegion is { } backup
            && Regions.FirstOrDefault(r => r.Code == backup) is { } backupRegion)
        {
            rows.Add(new(backupRegion.Code, $"{Math.Max(p.LatencyMs, backupRegion.LatencyMs) + 18} ms"));
        }

        return [.. rows];
    }

    public DashboardDatabase? FindDatabase(string databaseId) =>
        Databases.FirstOrDefault(d => d.Id == databaseId);

    public (string Schedule, string Retention)? BackupPolicy(string databaseId) =>
        World.BackupPolicies.TryGetValue(databaseId, out var policy) ? policy : null;

    public void SaveBackupPolicy(string databaseId, string schedule, string retention) =>
        World.BackupPolicies[databaseId] = (schedule, retention);

    public bool BackupsEnabled(string databaseId) =>
        FindDatabase(databaseId)?.BackupRegion is not null && !World.BackupsDisabled.Contains(databaseId);

    public void SetBackupsEnabled(string databaseId, bool enabled)
    {
        if (!enabled)
        {
            World.BackupsDisabled.Add(databaseId);
        }
        else
        {
            World.BackupsDisabled.Remove(databaseId);
        }
    }
}
