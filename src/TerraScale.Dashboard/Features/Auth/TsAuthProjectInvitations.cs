using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public partial interface ITsAuthPlatform
    {
        Task<TsAuthPlatformPage<TsAuthPlatformInvitation>> ListProjectInvitationsAsync(SessionRecord session,
            string organizationId, string projectId, string? after, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway
    {
        public Task<TsAuthPlatformPage<TsAuthPlatformInvitation>> ListProjectInvitationsAsync(SessionRecord session,
            string organizationId, string projectId, string? after, CancellationToken cancellationToken)
        {
            return PlatformAsync(() => Client().Platform.ListInvitationsAsync(Bearer(session), Id(organizationId), 25, after, Id(projectId), cancellationToken));
        }
    }
}
