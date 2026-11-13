using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthProjects
{
    Task<TsAuthUserApiKeyPage> ListUserApiKeysAsync(SessionRecord session, string projectId, string userId, CancellationToken cancellationToken);
    Task RevokeUserApiKeyAsync(SessionRecord session, string projectId, string userId, string keyId, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TsAuthUserApiKeyPage> ListUserApiKeysAsync(SessionRecord session, string projectId, string userId, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListUserApiKeysAsync(Bearer(session), Id(projectId), Id(userId), cancellationToken));

    public Task RevokeUserApiKeyAsync(SessionRecord session, string projectId, string userId, string keyId, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.RevokeUserApiKeyAsync(Bearer(session), Id(projectId), Id(userId), Id(keyId), cancellationToken); return true; });
}
