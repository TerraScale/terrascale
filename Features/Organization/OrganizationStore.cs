using System.Collections.Immutable;
namespace TerraScale.Dashboard2;

public sealed partial class DashboardStore
{
    public string OrganizationCreatedAt => "2026-01-10T16:02:00Z";

    public string OrganizationStatus => "Active";

    public ImmutableArray<string> RegionCatalog => [.. Regions.Select(r => r.Code)];

    public void RenameProject(string projectId, string name) =>
        Projects = [.. Projects.Select(p => p.Id == projectId ? p with { Name = name.Trim() } : p)];

    public void UpdateMemberRole(string userId, string role) { }  // demo: roles render statically
}
