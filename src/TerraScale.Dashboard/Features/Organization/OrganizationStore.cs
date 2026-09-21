using System.Collections.Immutable;
namespace TerraScale.Dashboard;

public sealed partial class DashboardStore
{
    public sealed record OrganizationRegion(string Code, string Name);

    /// <summary>Pending ownership transfer; the demo store issues no secret
    /// code — the confirmation step only needs the expiry (v1 email flow).</summary>
    public sealed record OwnershipTransfer(string TargetUserId, string ExpiresAt);

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

    /// <summary>Projects as the v1 organization API returns them: with slug,
    /// backup region, and primary regions (organization-data.ts). The v1 dump
    /// world has the current project at slug "core" / GRU / ["gru"].</summary>
    public ImmutableArray<DashboardProject> OrganizationProjects =>
        [.. Projects.Select(WithPlacement)];

    private static DashboardProject WithPlacement(DashboardProject project)
    {
        var backup = project.IsDefault ? "GRU" : project.Region switch
        {
            "eu-central-1" => "FRA",
            "sa-east-1" => "GRU",
            _ => "SFO",
        };
        return project with
        {
            Slug = project.Slug is { Length: > 0 } ? project.Slug
                : project.IsDefault ? "core" : project.Name.Split('-')[0].ToLowerInvariant(),
            BackupRegion = project.BackupRegion.Length > 0 ? project.BackupRegion : backup,
            Regions = project.Regions.IsDefaultOrEmpty ? [backup.ToLowerInvariant()] : project.Regions,
        };
    }

    /// <summary>Port of renameOrganizationProjectAction: renames and, like the
    /// v1 inline form, also persists the placement selection.</summary>
    public void RenameProject(string projectId, string name, string? backupRegion, ImmutableArray<string>? regions) =>
        Projects = [.. Projects.Select(p =>
        {
            if (p.Id != projectId) return p;
            var updated = p with { Name = name.Trim() };
            if (regions is { } selected && !selected.IsDefaultOrEmpty)
            {
                updated = updated with { Regions = [.. selected.Select(r => r.ToLowerInvariant())] };
            }

            if (backupRegion is { Length: > 0 } backup)
            {
                updated = updated with { BackupRegion = backup.ToUpperInvariant() };
            }

            return updated;
        })];

    private ImmutableDictionary<string, OwnershipTransfer> ownershipTransfers =
        ImmutableDictionary<string, OwnershipTransfer>.Empty;

    public ImmutableArray<OwnershipTransfer> OrganizationOwnershipTransfers => [.. ownershipTransfers.Values];

    public void RequestOwnershipTransfer(string targetUserId) =>
        ownershipTransfers = ownershipTransfers.SetItem(
            targetUserId, new(targetUserId, DateTimeOffset.UtcNow.AddDays(7).ToString("O")));

    public void ConfirmOwnershipTransfer(string targetUserId)
    {
        ownershipTransfers = ownershipTransfers.Remove(targetUserId);
        UpdateMemberRole(targetUserId, "owner");
        UpdateMemberRole(CurrentUser.Id, "member");
    }
}
