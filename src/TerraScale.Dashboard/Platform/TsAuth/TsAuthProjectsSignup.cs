using System.Net;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed partial class TsAuthProjectsClient
{
    public Task<TsAuthPlatformPage<TsAuthProjectInvitation>> ListInvitationsAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/invitations" + PageQuery("q", null, limit, after),
            Json.TsAuthPlatformPageTsAuthProjectInvitation, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthPlatformPage<TsAuthWaitlistEntry>> ListWaitlistAsync(string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/waitlist" + PageQuery("q", null, limit, after),
            Json.TsAuthPlatformPageTsAuthWaitlistEntry, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthProjectInvitation> CreateInvitationAsync(string accessToken, string projectId, TsAuthCreateProjectInvitation request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/invitations"), request, Json.TsAuthCreateProjectInvitation,
            Json.TsAuthProjectInvitation, TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, MaxRequestBytes, cancellationToken);

    public Task RevokeInvitationAsync(string accessToken, string projectId, string invitationId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/invitations/" + Id(invitationId), cancellationToken);

    public Task ApproveWaitlistAsync(string accessToken, string projectId, string entryId, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/waitlist/" + Id(entryId) + "/approve"), null, Json.TsAuthPlatformName,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

    public Task RejectWaitlistAsync(string accessToken, string projectId, string entryId, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/waitlist/" + Id(entryId) + "/reject"), null, Json.TsAuthPlatformName,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

}
