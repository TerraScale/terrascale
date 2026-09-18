using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

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
        var nearest = store.Regions.First(r => r.Code == database.PrimaryRegion);
        return new(
            Database: row,
            PlacementLock: string.Join(", ", row.Regions.Split(',')),
            PlacementStatus: "Locked",
            PlacementGeneration: 2,
            Latency: latency,
            LatencyCapturedAt: "2026-09-18T07:00:00Z",
            NearestRegion: nearest.Code,
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

public sealed class DatabaseDetailPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/databases/{databaseId}");
        Summary("Renders the database detail page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var databaseId = HttpContext.Request.RouteValues["databaseId"] as string ?? "";
        var model = DatabaseDetailPageModel.From(
            store,
            databaseId,
            HttpContext.Request.Query["branched"].FirstOrDefault() == "1");
        return Task.FromResult<IResult>(model is null
            ? Results.NotFound()
            : DatabaseDetailSlice.Create(model));
    }
}
