using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthPlatform
{
    Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session,
        string organizationId, string projectId, string? after, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(SessionRecord session,
        string organizationId, string projectId, string? after, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.ListProjectMembersAsync(Bearer(session), Id(organizationId), Id(projectId), 25, after, cancellationToken));
}
