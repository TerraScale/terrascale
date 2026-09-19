using System.Collections.Immutable;
using TerraScale.Dashboard2.Features.Databases;

namespace TerraScale.Dashboard2;

public sealed partial class DashboardStore
{
    /// <summary>Maps a stored database to the list/detail view the original
    /// DatabaseResource carries (provisioning-state status + replication label).</summary>
    public DatabaseRow ToRow(DashboardDatabase database)
    {
        var regions = RegionsOf(database);

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

    /// <summary>Maps a stored cloud region id onto the v1 hub universe
    /// (the API only accepts gru|sfo|fra; unknown codes pass through).</summary>
    public static string HubCode(string region) => region.ToLowerInvariant() switch
    {
        "us-east-1" or "us-west-2" or "sfo" => "sfo",
        "eu-central-1" or "eu-west-1" or "fra" => "fra",
        "sa-east-1" or "gru" => "gru",
        var other => other,
    };

    /// <summary>The database's placement regions as deduped hub codes
    /// (v1 dedupeRegionCodes over the mapped regions).</summary>
    public static List<string> RegionsOf(DashboardDatabase database)
    {
        var regions = new List<string> { HubCode(database.PrimaryRegion) };
        if (database.BackupRegion is { } backup)
        {
            regions.Add(HubCode(backup));
        }
        return [.. regions.Distinct()];
    }

    /// <summary>Inter-region round-trip estimates for the detail page latency
    /// table: the project's first region is the nearest (0 ms); every other
    /// region has no measured estimate yet.</summary>
    public ImmutableArray<LatencyRow> LatencyFor(DashboardDatabase database)
    {
        return [.. RegionsOf(database).Select((region, index) => new LatencyRow(region, index == 0 ? "0 ms" : "-"))];
    }

    public DashboardDatabase? FindDatabase(string databaseId) =>
        Databases.FirstOrDefault(d => d.Id == databaseId);

    private readonly HashSet<string> backupsDisabled = [];

    private readonly Dictionary<string, (string Schedule, string Retention)> backupPolicies = [];

    public (string Schedule, string Retention)? BackupPolicy(string databaseId) =>
        backupPolicies.TryGetValue(databaseId, out var policy) ? policy : null;

    public void SaveBackupPolicy(string databaseId, string schedule, string retention) =>
        backupPolicies[databaseId] = (schedule, retention);

    public bool BackupsEnabled(string databaseId) =>
        FindDatabase(databaseId)?.BackupRegion is not null && !backupsDisabled.Contains(databaseId);

    public void SetBackupsEnabled(string databaseId, bool enabled)
    {
        if (!enabled)
        {
            backupsDisabled.Add(databaseId);
        }
        else
        {
            backupsDisabled.Remove(databaseId);
        }
    }
}
