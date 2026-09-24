using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Estate;

public sealed record AdminEstatePageModel(
    string Tab,
    string Query,
    string? Notice,
    IReadOnlyList<AdminRegionRow> Regions,
    IReadOnlyList<AdminHostRow> Hosts,
    int AvailableRegionCount,
    int RegionPage,
    int RegionTotalPages,
    int RegionTotalRows,
    int HostPage,
    int HostTotalPages,
    int HostTotalRows,
    ImmutableDictionary<string, ImmutableArray<DashboardStore.AdminEstateFact>> Facts,
    int AvailableSections,
    int TotalSections) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
