using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed record DatabaseDetailPageModel(
    DatabaseRow Database,
    string PlacementLock,
    string PlacementStatus,
    int PlacementGeneration,
    ImmutableArray<LatencyRow> Latency,
    string LatencyCapturedAt,
    string NearestRegion,
    bool BranchPhysicalClone,
    bool BranchPromotion,
    bool BranchMerge,
    bool BackupsEnabled,
    string BackupCron,
    int BackupRetentionDays,
    bool BranchRequested) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static DatabaseDetailPageModel? From(DashboardStore store, string databaseId, bool branchRequested)
    {
        var database = store.FindDatabase(databaseId);
        if (database is null)
        {
            return null;
        }

        var row = store.ToRow(database);
        var latency = store.LatencyFor(database);
        // Fresh projects sit in authority regions outside the placement catalog.
        var nearest = store.Regions.FirstOrDefault(r => r.Code == database.PrimaryRegion);
        return new(
            Database: row,
            PlacementLock: string.Join(", ", row.Regions.Split(',')),
            PlacementStatus: "Locked",
            PlacementGeneration: 2,
            Latency: latency,
            LatencyCapturedAt: "2026-09-18T07:00:00Z",
            NearestRegion: nearest?.Code ?? database.PrimaryRegion,
            BranchPhysicalClone: database.Status == "Active",
            BranchPromotion: false,
            BranchMerge: false,
            BackupsEnabled: store.BackupsEnabled(database.Id),
            BackupCron: "0 2 * * *",
            BackupRetentionDays: 7,
            BranchRequested: branchRequested)
        {
            Shell = ShellFactory.Create(store, "/databases"),
        };
    }
}
