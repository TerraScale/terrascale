using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed record OrganizationPageModel(
    string OrganizationId,
    string OrganizationName,
    string OrganizationCreatedAt,
    string OrganizationStatus,
    string CurrentProjectId,
    string SignedInUserId,
    string SignedInEmail,
    bool CanManageOrganization,
    bool DeletionRequested,
    ImmutableArray<DashboardProject> Projects,
    ImmutableArray<TeamMember> Users,
    ImmutableArray<Invitation> Invitations,
    ImmutableArray<DashboardStore.OwnershipTransfer> OwnershipTransfers,
    ImmutableArray<DashboardStore.OrganizationRegion> RegionCatalog,
    string SuggestedRegion) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static OrganizationPageModel From(DashboardStore store, string href)
    {
        var user = store.CurrentUser;
        return new(
            OrganizationId: store.ActiveOrganizationId,
            OrganizationName: store.ActiveOrganizationName,
            OrganizationCreatedAt: store.OrganizationCreatedAt,
            OrganizationStatus: store.OrganizationStatus,
            CurrentProjectId: store.ActiveProjectId,
            SignedInUserId: user.Id,
            SignedInEmail: user.Email,
            CanManageOrganization: store.OwnerSession,
            DeletionRequested: store.OrganizationDeletionRequested,
            Projects: store.OrganizationProjects,
            Users: store.TeamMembers,
            Invitations: store.Invitations,
            OwnershipTransfers: store.OrganizationOwnershipTransfers,
            RegionCatalog: store.OrganizationRegions,
            SuggestedRegion: store.SuggestedRegion)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
