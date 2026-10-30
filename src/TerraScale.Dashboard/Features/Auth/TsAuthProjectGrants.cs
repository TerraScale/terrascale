using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public partial interface ITsAuthPlatform
    {
        Task<TsAuthPlatformPage<TsAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session,
            string organizationId, string projectId, string? after, CancellationToken cancellationToken);
        Task<TsAuthPlatformProjectMember> SetProjectMemberRoleAsync(SessionRecord session, string organizationId,
            string projectId, string userId, string role, CancellationToken cancellationToken);
        Task RemoveProjectMemberAsync(SessionRecord session, string organizationId, string projectId, string userId, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway
    {
        public Task<TsAuthPlatformPage<TsAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session,
            string organizationId, string projectId, string? after, CancellationToken cancellationToken)
        {
            return PlatformAsync(() => Client().Platform.ListProjectMembersAsync(Bearer(session), Id(organizationId), Id(projectId), 25, after, cancellationToken));
        }

        public Task<TsAuthPlatformProjectMember> SetProjectMemberRoleAsync(SessionRecord session, string organizationId,
            string projectId, string userId, string role, CancellationToken cancellationToken)
        {
            return PlatformAsync(() => Client().Platform.SetProjectMemberRoleAsync(Bearer(session), Id(organizationId), Id(projectId), Id(userId), new(role), cancellationToken));
        }

        public Task RemoveProjectMemberAsync(SessionRecord session, string organizationId, string projectId, string userId, CancellationToken cancellationToken)
        {
            return PlatformAsync(() => Client().Platform.RemoveProjectMemberAsync(Bearer(session), Id(organizationId), Id(projectId), Id(userId), cancellationToken));
        }
    }
}
