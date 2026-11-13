using System.Net;

namespace TerraScale.DotNet;

/// <summary>Project invitations and waitlist management.</summary>
public sealed partial class TerraScaleAuthProjectsClient
{
    /// <summary>List a cursor page of project invitations.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectInvitation>> ListInvitationsAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/invitations" + PageQuery("q", null, limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectInvitation, HttpStatusCode.OK, cancellationToken);

    /// <summary>List a cursor page of project waitlist.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthWaitlistEntry>> ListWaitlistAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/waitlist" + PageQuery("q", null, limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthWaitlistEntry, HttpStatusCode.OK, cancellationToken);

    /// <summary>Invite an end user; admin access required.</summary>
    public Task<TerraScaleAuthProjectInvitation> CreateInvitationAsync(string accessToken, string projectId, TerraScaleAuthCreateProjectInvitation request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/invitations"), request, Json.TerraScaleAuthCreateProjectInvitation,
            Json.TerraScaleAuthProjectInvitation, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, MaxRequestBytes, cancellationToken);

    /// <summary>Revoke a pending invitation.</summary>
    public Task RevokeInvitationAsync(string accessToken, string projectId, string invitationId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/invitations/" + Id(invitationId), cancellationToken);

    /// <summary>Approve a waitlist entry; admin access required.</summary>
    public Task ApproveWaitlistAsync(string accessToken, string projectId, string entryId, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/waitlist/" + Id(entryId) + "/approve"), null, Json.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

    /// <summary>Reject a waitlist entry; admin access required.</summary>
    public Task RejectWaitlistAsync(string accessToken, string projectId, string entryId, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/waitlist/" + Id(entryId) + "/reject"), null, Json.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

}
