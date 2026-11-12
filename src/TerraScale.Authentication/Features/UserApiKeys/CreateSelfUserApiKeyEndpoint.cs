using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class CreateSelfUserApiKeyEndpoint(PlatformAuthentication authentication, UserApiKeys keys) : SelfUserApiKeyEndpoint(authentication)
{
    public override void Configure()
    {
        Post(Root + "");
        this.DescribeSelf("create_self_user_api_key", "Create self user api key", typeof(UserApiKeyRequest), 201, typeof(UserApiKeyBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var request = await this.BodyAsync(UserApiKeyJson.Default.UserApiKeyRequest, cancellationToken);
        return Results.Json(await keys.CreateAsync(this.RouteId("project"), caller.Id, caller.Id, request, cancellationToken), UserApiKeyJson.Default.UserApiKeyBody, statusCode: 201);
    }
}
