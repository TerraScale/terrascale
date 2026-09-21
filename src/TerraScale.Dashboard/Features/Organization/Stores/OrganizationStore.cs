using System.Collections.Immutable;
namespace TerraScale.Dashboard.Shared.State;

public sealed partial class DashboardStore
{
    public sealed record OrganizationRegion(string Code, string Name, string Country, int LatencyMs);

    /// <summary>Pending ownership transfer; the demo store issues no secret
    /// code — the confirmation step only needs the expiry (v1 email flow).</summary>
    public sealed record OwnershipTransfer(string TargetUserId, string ExpiresAt);

    public string OrganizationCreatedAt => ActiveOrganization.CreatedAt.ToString("O");

    public string OrganizationStatus => ActiveOrganization.Archived ? "Archived" : "Active";

    /// <summary>Region catalog from src/backend/regions/catalog.ts (GRU/SFO/FRA)
    /// plus the ISO country and the demo round-trip latency. Live mode reads the
    /// country from the SDK region catalog and the latency from measured RTT;
    /// the demo world seeds the same shape in memory.</summary>
    public ImmutableArray<OrganizationRegion> OrganizationRegions { get; } =
    [
        new("GRU", "Sao Paulo", "BR", 118),
        new("SFO", "San Francisco", "US", 28),
        new("FRA", "Frankfurt", "DE", 74),
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

    private Dictionary<string, OwnershipTransfer> ownershipTransfers => ActiveOrganization.OwnershipTransfers;

    public ImmutableArray<OwnershipTransfer> OrganizationOwnershipTransfers => [.. ownershipTransfers.Values];

    public void RequestOwnershipTransfer(string targetUserId)
    {
        if (IsPersonalOrganization) return;
        ownershipTransfers[targetUserId] = new(targetUserId, DateTimeOffset.UtcNow.AddDays(7).ToString("O"));
    }

    public void ConfirmOwnershipTransfer(string targetUserId)
    {
        if (IsPersonalOrganization) return;
        ownershipTransfers.Remove(targetUserId);
        UpdateMemberRole(targetUserId, "owner");
        UpdateMemberRole(CurrentUser.Id, "member");
    }
}
