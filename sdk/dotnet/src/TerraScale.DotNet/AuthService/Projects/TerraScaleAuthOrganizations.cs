using System.Net;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthProjectsClient
{
    /// <summary>POST /v1/{project}/organizations; requires auth:write.</summary>
    public Task<TerraScaleAuthOrganization> CreateOrganizationAsync(string accessToken, string projectId, TerraScaleAuthOrganizationRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/organizations"), request,
            TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationRequest, TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    /// <summary>GET /v1/{project}/organizations; requires auth:read.</summary>
    public Task<TerraScaleAuthOrganizationPage> ListOrganizationsAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/organizations" + PageQuery("q", null, limit, after), TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationPage, HttpStatusCode.OK, cancellationToken);

    /// <summary>GET /v1/{project}/organizations/{org}; requires auth:read.</summary>
    public Task<TerraScaleAuthOrganization> GetOrganizationAsync(string accessToken, string projectId, string org, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}", TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganization, HttpStatusCode.OK, cancellationToken);

    /// <summary>PUT /v1/{project}/organizations/{org}; requires auth:write.</summary>
    public Task<TerraScaleAuthOrganization> UpdateOrganizationAsync(string accessToken, string projectId, string org, TerraScaleAuthOrganizationRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}"), request,
            TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationRequest, TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 24 * 1024, cancellationToken);

    /// <summary>DELETE /v1/{project}/organizations/{org}; requires auth:write.</summary>
    public Task DeleteOrganizationAsync(string accessToken, string projectId, string org, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}", cancellationToken);

    /// <summary>POST /v1/{project}/organizations/{org}/memberships; requires auth:write.</summary>
    public Task<TerraScaleAuthOrganizationMembership> AddOrganizationMemberAsync(string accessToken, string projectId, string org, TerraScaleAuthOrganizationMembershipRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships"), request,
            TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationMembershipRequest, TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationMembership,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    /// <summary>GET /v1/{project}/organizations/{org}/memberships; requires auth:read.</summary>
    public Task<TerraScaleAuthOrganizationMembershipPage> ListOrganizationMembersAsync(string accessToken, string projectId, string org, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships" + PageQuery("q", null, limit, after), TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationMembershipPage, HttpStatusCode.OK, cancellationToken);

    /// <summary>PUT /v1/{project}/organizations/{org}/memberships/{user}; requires auth:write.</summary>
    public Task<TerraScaleAuthOrganizationMembership> UpdateOrganizationMemberAsync(string accessToken, string projectId, string org, string user, TerraScaleAuthOrganizationMembershipRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships/{Uri.EscapeDataString(user)}"), request,
            TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationMembershipRequest, TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationMembership,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 24 * 1024, cancellationToken);

    /// <summary>DELETE /v1/{project}/organizations/{org}/memberships/{user}; requires auth:write.</summary>
    public Task DeleteOrganizationMemberAsync(string accessToken, string projectId, string org, string user, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/memberships/{Uri.EscapeDataString(user)}", cancellationToken);

    /// <summary>POST /v1/{project}/organizations/{org}/invitations; requires auth:write.</summary>
    public Task<TerraScaleAuthOrganizationInvitation> CreateOrganizationInvitationAsync(string accessToken, string projectId, string org, TerraScaleAuthOrganizationInvitationRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/invitations"), request,
            TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationInvitationRequest, TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationInvitation,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    /// <summary>GET /v1/{project}/organizations/{org}/invitations; requires auth:read.</summary>
    public Task<TerraScaleAuthOrganizationInvitationPage> ListOrganizationInvitationsAsync(string accessToken, string projectId, string org, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/invitations" + PageQuery("q", null, limit, after), TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationInvitationPage, HttpStatusCode.OK, cancellationToken);

    /// <summary>DELETE /v1/{project}/organizations/{org}/invitations/{invitation}; requires auth:write.</summary>
    public Task RevokeOrganizationInvitationAsync(string accessToken, string projectId, string org, string invitation, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/invitations/{Uri.EscapeDataString(invitation)}", cancellationToken);

    /// <summary>POST /v1/{project}/organizations/{org}/domains; requires auth:write.</summary>
    public Task<TerraScaleAuthOrganizationDomain> AddOrganizationDomainAsync(string accessToken, string projectId, string org, TerraScaleAuthOrganizationDomainRequest request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains"), request,
            TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationDomainRequest, TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationDomain,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 24 * 1024, cancellationToken);

    /// <summary>GET /v1/{project}/organizations/{org}/domains; requires auth:read.</summary>
    public Task<TerraScaleAuthOrganizationDomainPage> ListOrganizationDomainsAsync(string accessToken, string projectId, string org, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains", TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationDomainPage, HttpStatusCode.OK, cancellationToken);

    /// <summary>POST /v1/{project}/organizations/{org}/domains/{domain}/verify; requires auth:write.</summary>
    public Task<TerraScaleAuthOrganizationDomain> VerifyOrganizationDomainAsync(string accessToken, string projectId, string org, string domain, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains/{Uri.EscapeDataString(domain)}/verify", TerraScaleAuthOrganizationsJson.Default.TerraScaleAuthOrganizationDomain, HttpStatusCode.OK, cancellationToken);

    /// <summary>DELETE /v1/{project}/organizations/{org}/domains/{domain}; requires auth:write.</summary>
    public Task DeleteOrganizationDomainAsync(string accessToken, string projectId, string org, string domain, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, $"/organizations/{Uri.EscapeDataString(org)}/domains/{Uri.EscapeDataString(domain)}", cancellationToken);

}
