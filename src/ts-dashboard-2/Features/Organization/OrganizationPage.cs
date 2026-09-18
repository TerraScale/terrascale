using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

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
    ImmutableArray<DashboardProject> Projects,
    ImmutableArray<TeamMember> Users,
    ImmutableArray<Invitation> Invitations,
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
            Projects: store.Projects,
            Users: store.TeamMembers,
            Invitations: store.Invitations,
            RegionCatalog: store.OrganizationRegions,
            SuggestedRegion: store.SuggestedRegion)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}

public sealed class OrganizationPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/organization");
        Summary("Renders the Organization page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(OrganizationSlice.Create(OrganizationPageModel.From(store, "/organization")));
}
