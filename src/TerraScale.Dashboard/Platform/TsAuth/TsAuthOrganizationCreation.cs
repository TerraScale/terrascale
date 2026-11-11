using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth;

public partial interface ITsAuthPlatform
{
    Task<TsAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway
{
    public Task<TsAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, string idempotencyKey, CancellationToken cancellationToken)
    {
        return PlatformAsync(() => Client().Platform.CreateOrganizationAsync(Bearer(session), new(name, idempotencyKey), cancellationToken));
    }
}
