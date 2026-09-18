using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Admin;

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
