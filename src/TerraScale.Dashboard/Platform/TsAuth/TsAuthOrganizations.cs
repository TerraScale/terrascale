using System.Net;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed partial class TsAuthProjectsClient
{
    public Task<TsAuthOrganization> CreateOrganizationAsync(string accessToken, string projectId, TsAuthOrganizationRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/organizations"), request,
            TsAuthOrganizationsJson.Default.TsAuthOrganizationRequest, TsAuthOrganizationsJson.Default.TsAuthOrganization,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    public Task<TsAuthOrganizationPage> ListOrganizationsAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/organizations" + PageQuery("q", null, limit, after), TsAuthOrganizationsJson.Default.TsAuthOrganizationPage, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthOrganization> GetOrganizationAsync(string accessToken, string projectId, string org, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}", TsAuthOrganizationsJson.Default.TsAuthOrganization, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthOrganization> UpdateOrganizationAsync(string accessToken, string projectId, string org, TsAuthOrganizationRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}"), request,
            TsAuthOrganizationsJson.Default.TsAuthOrganizationRequest, TsAuthOrganizationsJson.Default.TsAuthOrganization,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 24 * 1024, cancellationToken);

    public Task DeleteOrganizationAsync(string accessToken, string projectId, string org, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}", cancellationToken);

    public Task<TsAuthOrganizationMembership> AddOrganizationMemberAsync(string accessToken, string projectId, string org, TsAuthOrganizationMembershipRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships"), request,
            TsAuthOrganizationsJson.Default.TsAuthOrganizationMembershipRequest, TsAuthOrganizationsJson.Default.TsAuthOrganizationMembership,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    public Task<TsAuthOrganizationMembershipPage> ListOrganizationMembersAsync(string accessToken, string projectId, string org, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships" + PageQuery("q", null, limit, after), TsAuthOrganizationsJson.Default.TsAuthOrganizationMembershipPage, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthOrganizationMembership> UpdateOrganizationMemberAsync(string accessToken, string projectId, string org, string user, TsAuthOrganizationMembershipRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships/{Uri.EscapeDataString(user)}"), request,
            TsAuthOrganizationsJson.Default.TsAuthOrganizationMembershipRequest, TsAuthOrganizationsJson.Default.TsAuthOrganizationMembership,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 24 * 1024, cancellationToken);

    public Task DeleteOrganizationMemberAsync(string accessToken, string projectId, string org, string user, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships/{Uri.EscapeDataString(user)}", cancellationToken);

    public Task<TsAuthOrganizationInvitation> CreateOrganizationInvitationAsync(string accessToken, string projectId, string org, TsAuthOrganizationInvitationRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/invitations"), request,
            TsAuthOrganizationsJson.Default.TsAuthOrganizationInvitationRequest, TsAuthOrganizationsJson.Default.TsAuthOrganizationInvitation,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    public Task<TsAuthOrganizationInvitationPage> ListOrganizationInvitationsAsync(string accessToken, string projectId, string org, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/invitations" + PageQuery("q", null, limit, after), TsAuthOrganizationsJson.Default.TsAuthOrganizationInvitationPage, HttpStatusCode.OK, cancellationToken);

    public Task RevokeOrganizationInvitationAsync(string accessToken, string projectId, string org, string invitation, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/invitations/{Uri.EscapeDataString(invitation)}", cancellationToken);

    public Task<TsAuthOrganizationDomain> AddOrganizationDomainAsync(string accessToken, string projectId, string org, TsAuthOrganizationDomainRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains"), request,
            TsAuthOrganizationsJson.Default.TsAuthOrganizationDomainRequest, TsAuthOrganizationsJson.Default.TsAuthOrganizationDomain,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    public Task<TsAuthOrganizationDomainPage> ListOrganizationDomainsAsync(string accessToken, string projectId, string org, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains", TsAuthOrganizationsJson.Default.TsAuthOrganizationDomainPage, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthOrganizationDomain> VerifyOrganizationDomainAsync(string accessToken, string projectId, string org, string domain, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains/{Uri.EscapeDataString(domain)}/verify", TsAuthOrganizationsJson.Default.TsAuthOrganizationDomain, HttpStatusCode.OK, cancellationToken);

    public Task DeleteOrganizationDomainAsync(string accessToken, string projectId, string org, string domain, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains/{Uri.EscapeDataString(domain)}", cancellationToken);

}
