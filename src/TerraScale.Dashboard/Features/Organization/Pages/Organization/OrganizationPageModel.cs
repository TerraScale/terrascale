using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Organization.Pages.Organization;

public sealed record OrganizationPageModel(
    string OrganizationId,
    string OrganizationName,
    string OrganizationCreatedAt,
    string OrganizationStatus,
    OrganizationKind OrganizationKind,
    bool FreeAllowanceEligible,
    ImmutableArray<OrganizationWorld> Organizations,
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
    string SuggestedRegion,
    NewOrganizationWizardModel NewOrganization) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static OrganizationPageModel From(DashboardStore store, string href, NewOrganizationWizardModel newOrganization)
    {
        var user = store.CurrentUser;
        return new(
            OrganizationId: store.ActiveOrganizationId,
            OrganizationName: store.ActiveOrganizationName,
            OrganizationCreatedAt: store.OrganizationCreatedAt,
            OrganizationStatus: store.OrganizationStatus,
            OrganizationKind: store.ActiveOrganization.Kind,
            FreeAllowanceEligible: store.FreeAllowanceEligible,
            Organizations: store.Organizations,
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
            SuggestedRegion: store.SuggestedRegion,
            NewOrganization: newOrganization)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
