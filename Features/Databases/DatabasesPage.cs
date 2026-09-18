using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed record DatabasesPageModel(
    ImmutableArray<DatabaseRow> Databases,
    ImmutableArray<RegionInfo> RegionCatalog,
    ImmutableArray<string> ProjectRegions,
    string Query,
    bool CreateOpen,
    string? Error) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static DatabasesPageModel From(DashboardStore store, string? query, bool createOpen, string? error)
    {
        var project = store.ActiveProject;
        var rows = store.ProjectDatabases(project.Id).Select(store.ToRow);
        var term = (query ?? "").Trim().ToLowerInvariant();
        if (term.Length > 0)
        {
            rows = rows.Where(row => $"{row.Name} {row.Id} {row.Regions}".ToLowerInvariant().Contains(term));
        }

        return new(
            Databases: [.. rows],
            RegionCatalog: store.Regions,
            ProjectRegions: [project.Region],
            Query: query ?? "",
            CreateOpen: createOpen,
            Error: error)
        {
            Shell = ShellFactory.Create(store, "/databases"),
        };
    }

    public static string FormatUtc(string iso) =>
        DateTimeOffset.TryParse(iso, out var time) ? time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'") : iso;

    public static string RegionDisplay(string code) => code.ToUpperInvariant() + " · " + RegionName(code);

    private static string RegionName(string code) => code switch
    {
        "us-east-1" => "Virginia, US",
        "us-west-2" => "Oregon, US",
        "eu-central-1" => "Frankfurt, DE",
        "eu-west-1" => "Dublin, IE",
        "sa-east-1" => "São Paulo, BR",
        _ => code,
    };
}

public sealed class DatabasesPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/databases");
        Summary("Renders the Databases page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(DatabasesSlice.Create(DatabasesPageModel.From(
            store,
            HttpContext.Request.Query["q"].FirstOrDefault(),
            HttpContext.Request.Query["create"].FirstOrDefault() == "1",
            HttpContext.Request.Query["error"].FirstOrDefault())));
}
