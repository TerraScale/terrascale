using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthPlatform
{
    Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session,
        string organizationId, string projectId, string? after, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformProjectMember> SetProjectMemberRoleAsync(SessionRecord session, string organizationId,
        string projectId, string userId, string role, CancellationToken cancellationToken);
    Task RemoveProjectMemberAsync(SessionRecord session, string organizationId, string projectId, string userId, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session,
        string organizationId, string projectId, string? after, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.ListProjectMembersAsync(Bearer(session), Id(organizationId), Id(projectId), 25, after, cancellationToken));

    public Task<TerraScaleAuthPlatformProjectMember> SetProjectMemberRoleAsync(SessionRecord session, string organizationId,
        string projectId, string userId, string role, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.SetProjectMemberRoleAsync(Bearer(session), Id(organizationId), Id(projectId), Id(userId), new(role), cancellationToken));

    public Task RemoveProjectMemberAsync(SessionRecord session, string organizationId, string projectId, string userId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.RemoveProjectMemberAsync(Bearer(session), Id(organizationId), Id(projectId), Id(userId), cancellationToken));
}
