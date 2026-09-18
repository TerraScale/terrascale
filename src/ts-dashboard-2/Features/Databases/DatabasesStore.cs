namespace TerraScale.Dashboard2.Features.Databases
{
public sealed record DatabaseRow(
    string Id,
    string Name,
    string Regions,
    string? BackupRegion,
    string Replication,
    string Status,
    string StatusTone,
    bool Provisioning,
    string? OperationId,
    string CreatedAt,
    int Version);

public sealed record LatencyRow(string Region, string Estimate);
}

namespace TerraScale.Dashboard2
{
public sealed partial class DashboardStore
{
    /// <summary>Maps a stored database to the list/detail view the original
    /// DatabaseResource carries (provisioning-state status + replication label).</summary>
    public Features.Databases.DatabaseRow ToRow(DashboardDatabase database)
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
        return new Features.Databases.DatabaseRow(
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
    public System.Collections.Immutable.ImmutableArray<Features.Databases.LatencyRow> LatencyFor(DashboardDatabase database)
    {
        var primary = Regions.First(r => r.Code == database.PrimaryRegion);
        var rows = new List<Features.Databases.LatencyRow> { new(primary.Code, $"{primary.LatencyMs} ms") };
        if (database.BackupRegion is { } backup && Regions.FirstOrDefault(r => r.Code == backup) is { } backupRegion)
        {
            rows.Add(new(backupRegion.Code, $"{Math.Max(primary.LatencyMs, backupRegion.LatencyMs) + 18} ms"));
        }

        return [.. rows];
    }

    public DashboardDatabase? FindDatabase(string databaseId) =>
        Databases.FirstOrDefault(d => d.Id == databaseId);

    private readonly HashSet<string> backupsDisabled = [];

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
}
