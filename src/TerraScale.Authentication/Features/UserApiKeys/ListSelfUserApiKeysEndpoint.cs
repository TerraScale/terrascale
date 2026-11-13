using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class ListSelfUserApiKeysEndpoint(PlatformAuthentication authentication, UserApiKeys keys) : SelfUserApiKeyEndpoint(authentication)
{
    public override void Configure()
    {
        Get(Root + "");
        this.DescribeSelf("list_self_user_api_keys", "List self user api keys", null, 200, typeof(UserApiKeyPageBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        return Results.Json(await keys.ListAsync(this.RouteId("project"), caller.Id, cancellationToken), UserApiKeyJson.Default.UserApiKeyPageBody);
    }
}
