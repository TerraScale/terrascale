using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthProjects
{
    Task<TsAuthOrganization> CreateOrganizationAsync(SessionRecord session, string projectId, TsAuthOrganizationRequest request, CancellationToken cancellationToken);
    Task<TsAuthOrganizationPage> ListOrganizationsAsync(SessionRecord session, string projectId, int? limit, string? after, CancellationToken cancellationToken);
    Task<TsAuthOrganization> GetOrganizationAsync(SessionRecord session, string projectId, string org, CancellationToken cancellationToken);
    Task<TsAuthOrganization> UpdateOrganizationAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationRequest request, CancellationToken cancellationToken);
    Task DeleteOrganizationAsync(SessionRecord session, string projectId, string org, CancellationToken cancellationToken);
    Task<TsAuthOrganizationMembership> AddOrganizationMemberAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationMembershipRequest request, CancellationToken cancellationToken);
    Task<TsAuthOrganizationMembershipPage> ListOrganizationMembersAsync(SessionRecord session, string projectId, string org, int? limit, string? after, CancellationToken cancellationToken);
    Task<TsAuthOrganizationMembership> UpdateOrganizationMemberAsync(SessionRecord session, string projectId, string org, string user, TsAuthOrganizationMembershipRequest request, CancellationToken cancellationToken);
    Task DeleteOrganizationMemberAsync(SessionRecord session, string projectId, string org, string user, CancellationToken cancellationToken);
    Task<TsAuthOrganizationInvitation> CreateOrganizationInvitationAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationInvitationRequest request, CancellationToken cancellationToken);
    Task<TsAuthOrganizationInvitationPage> ListOrganizationInvitationsAsync(SessionRecord session, string projectId, string org, int? limit, string? after, CancellationToken cancellationToken);
    Task RevokeOrganizationInvitationAsync(SessionRecord session, string projectId, string org, string invitation, CancellationToken cancellationToken);
    Task<TsAuthOrganizationDomain> AddOrganizationDomainAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationDomainRequest request, CancellationToken cancellationToken);
    Task<TsAuthOrganizationDomainPage> ListOrganizationDomainsAsync(SessionRecord session, string projectId, string org, CancellationToken cancellationToken);
    Task<TsAuthOrganizationDomain> VerifyOrganizationDomainAsync(SessionRecord session, string projectId, string org, string domain, CancellationToken cancellationToken);
    Task DeleteOrganizationDomainAsync(SessionRecord session, string projectId, string org, string domain, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TsAuthOrganization> CreateOrganizationAsync(SessionRecord session, string projectId, TsAuthOrganizationRequest request, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.CreateOrganizationAsync(Bearer(session), Id(projectId), request, cancellationToken));

    public Task<TsAuthOrganizationPage> ListOrganizationsAsync(SessionRecord session, string projectId, int? limit, string? after, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListOrganizationsAsync(Bearer(session), Id(projectId), limit, after, cancellationToken));

    public Task<TsAuthOrganization> GetOrganizationAsync(SessionRecord session, string projectId, string org, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.GetOrganizationAsync(Bearer(session), Id(projectId), org, cancellationToken));

    public Task<TsAuthOrganization> UpdateOrganizationAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationRequest request, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.UpdateOrganizationAsync(Bearer(session), Id(projectId), org, request, cancellationToken));

    Task ITsAuthProjects.DeleteOrganizationAsync(SessionRecord session, string projectId, string org, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.DeleteOrganizationAsync(Bearer(session), Id(projectId), org, cancellationToken); return true; });

    public Task<TsAuthOrganizationMembership> AddOrganizationMemberAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationMembershipRequest request, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.AddOrganizationMemberAsync(Bearer(session), Id(projectId), org, request, cancellationToken));

    public Task<TsAuthOrganizationMembershipPage> ListOrganizationMembersAsync(SessionRecord session, string projectId, string org, int? limit, string? after, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListOrganizationMembersAsync(Bearer(session), Id(projectId), org, limit, after, cancellationToken));

    public Task<TsAuthOrganizationMembership> UpdateOrganizationMemberAsync(SessionRecord session, string projectId, string org, string user, TsAuthOrganizationMembershipRequest request, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.UpdateOrganizationMemberAsync(Bearer(session), Id(projectId), org, user, request, cancellationToken));

    public Task DeleteOrganizationMemberAsync(SessionRecord session, string projectId, string org, string user, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.DeleteOrganizationMemberAsync(Bearer(session), Id(projectId), org, user, cancellationToken); return true; });

    public Task<TsAuthOrganizationInvitation> CreateOrganizationInvitationAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationInvitationRequest request, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.CreateOrganizationInvitationAsync(Bearer(session), Id(projectId), org, request, cancellationToken));

    public Task<TsAuthOrganizationInvitationPage> ListOrganizationInvitationsAsync(SessionRecord session, string projectId, string org, int? limit, string? after, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListOrganizationInvitationsAsync(Bearer(session), Id(projectId), org, limit, after, cancellationToken));

    public Task RevokeOrganizationInvitationAsync(SessionRecord session, string projectId, string org, string invitation, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.RevokeOrganizationInvitationAsync(Bearer(session), Id(projectId), org, invitation, cancellationToken); return true; });

    public Task<TsAuthOrganizationDomain> AddOrganizationDomainAsync(SessionRecord session, string projectId, string org, TsAuthOrganizationDomainRequest request, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.AddOrganizationDomainAsync(Bearer(session), Id(projectId), org, request, cancellationToken));

    public Task<TsAuthOrganizationDomainPage> ListOrganizationDomainsAsync(SessionRecord session, string projectId, string org, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListOrganizationDomainsAsync(Bearer(session), Id(projectId), org, cancellationToken));

    public Task<TsAuthOrganizationDomain> VerifyOrganizationDomainAsync(SessionRecord session, string projectId, string org, string domain, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.VerifyOrganizationDomainAsync(Bearer(session), Id(projectId), org, domain, cancellationToken));

    public Task DeleteOrganizationDomainAsync(SessionRecord session, string projectId, string org, string domain, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.DeleteOrganizationDomainAsync(Bearer(session), Id(projectId), org, domain, cancellationToken); return true; });

}
