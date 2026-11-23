using System.Net;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthProjectsClient
{
    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthProjectInvitation>>> ListInvitationsAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/invitations" + PageQuery("q", null, limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectInvitation, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthWaitlistEntry>>> ListWaitlistAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/waitlist" + PageQuery("q", null, limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthWaitlistEntry, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthProjectInvitation>> CreateInvitationAsync(string accessToken, string projectId, TerraScaleAuthCreateProjectInvitation request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/invitations"), request, Json.TerraScaleAuthCreateProjectInvitation,
            Json.TerraScaleAuthProjectInvitation, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, MaxRequestBytes, cancellationToken);

    public Task<AuthOutcome> RevokeInvitationAsync(string accessToken, string projectId, string invitationId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/invitations/" + Id(invitationId), cancellationToken);

    public Task<AuthOutcome> ApproveWaitlistAsync(string accessToken, string projectId, string entryId, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/waitlist/" + Id(entryId) + "/approve"), null, Json.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

    public Task<AuthOutcome> RejectWaitlistAsync(string accessToken, string projectId, string entryId, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/waitlist/" + Id(entryId) + "/reject"), null, Json.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

}
