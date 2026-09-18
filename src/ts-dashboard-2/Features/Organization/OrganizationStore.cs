using System.Collections.Immutable;
namespace TerraScale.Dashboard2;

public sealed partial class DashboardStore
{
    public sealed record OrganizationRegion(string Code, string Name);

    public string OrganizationCreatedAt => "2026-01-10T16:02:00Z";

    public string OrganizationStatus => "Active";

    /// <summary>Region catalog from src/backend/regions/catalog.ts (GRU/SFO/FRA).</summary>
    public ImmutableArray<OrganizationRegion> OrganizationRegions { get; } =
    [
        new("GRU", "Sao Paulo"),
        new("SFO", "San Francisco"),
        new("FRA", "Frankfurt"),
    ];

    /// <summary>Suggested primary region, falling back to the catalog default.</summary>
    public string SuggestedRegion =>
        OrganizationRegions.FirstOrDefault(r => r.Code == ActiveProject.Region)?.Code
        ?? OrganizationRegions[0].Code;

    public void RenameProject(string projectId, string name) =>
        Projects = [.. Projects.Select(p => p.Id == projectId ? p with { Name = name.Trim() } : p)];
}
