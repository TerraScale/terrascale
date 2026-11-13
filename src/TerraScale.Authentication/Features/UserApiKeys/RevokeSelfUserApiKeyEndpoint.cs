using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class RevokeSelfUserApiKeyEndpoint(PlatformAuthentication authentication, UserApiKeys keys) : SelfUserApiKeyEndpoint(authentication)
{
    public override void Configure()
    {
        Delete(Root + "/{key}");
        this.DescribeSelf("revoke_self_user_api_key", "Revoke self user api key", null, 204, null);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await keys.RevokeAsync(this.RouteId("project"), caller.Id, this.RouteId("key"), caller.Id, cancellationToken);
        return Results.NoContent();
    }
}
