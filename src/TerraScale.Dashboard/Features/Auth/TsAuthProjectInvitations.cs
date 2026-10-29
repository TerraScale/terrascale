using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthPlatform
{
    Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> ListProjectInvitationsAsync(SessionRecord session,
        string organizationId, string projectId, string? after, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> ListProjectInvitationsAsync(SessionRecord session,
        string organizationId, string projectId, string? after, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.ListInvitationsAsync(Bearer(session), Id(organizationId), 25, after, Id(projectId), cancellationToken));
}
