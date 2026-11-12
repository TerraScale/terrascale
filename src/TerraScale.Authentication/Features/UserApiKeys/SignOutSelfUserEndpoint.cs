using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Hosting;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class SignOutSelfUserEndpoint(PlatformAuthentication authentication, Sessions sessions) : SelfUserApiKeyEndpoint(authentication)
{
    public override void Configure()
    {
        Post("/v1/{project}/me/sign-out");
        this.Describe("sign_out_self_user_everywhere", "Sign out everywhere",
            "Uses the project's hosted session cookie and requires the auth public Origin. Advances the user's auth epoch, ending every session and user-owned API key.",
            "User API keys", null, 204, null, conflict: true);
        Metadata(new RequiresSecurity(SecuritySchemes.SessionCookie));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await sessions.RevokeAllAsync(caller.InstanceId, caller.Id, cancellationToken);
        return Results.NoContent();
    }
}
