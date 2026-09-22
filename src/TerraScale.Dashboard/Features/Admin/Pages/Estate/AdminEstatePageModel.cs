using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Estate;

public sealed record AdminEstatePageModel(
    string Query,
    string? Notice,
    IReadOnlyList<AdminRegionRow> Regions,
    IReadOnlyList<AdminHostRow> Hosts,
    ImmutableDictionary<string, ImmutableArray<DashboardStore.AdminEstateFact>> Facts,
    int AvailableSections,
    int TotalSections) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
