using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class CreateSelfUserApiKeyEndpoint(PlatformAuthentication authentication, UserApiKeys keys) : SelfUserApiKeyEndpoint(authentication)
{
    protected override bool RequiresRecentSignIn => true;

    public override void Configure()
    {
        Post(Root + "");
        this.DescribeSelf("create_self_user_api_key", "Create self user api key", typeof(UserApiKeyRequest), 201, typeof(UserApiKeyBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var request = await this.BodyAsync(UserApiKeyJson.Default.UserApiKeyRequest, cancellationToken);
        var now = this.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (request.ExpiresAt > now.AddDays(365))
        {
            throw new ApiException(400, "invalid_request", "Self-service keys must expire within 365 days.");
        }

        request = request with { ExpiresAt = request.ExpiresAt ?? now.AddDays(90) };
        return Results.Json(await keys.CreateAsync(this.RouteId("project"), caller.Id, caller.Id, request, this.CreationAuthorization, cancellationToken), UserApiKeyJson.Default.UserApiKeyBody, statusCode: 201);
    }
}
