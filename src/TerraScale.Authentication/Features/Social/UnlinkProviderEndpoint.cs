using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Features.Social;

public sealed record UnlinkProviderRequest([property: JsonPropertyName("code")] string? Code);

public sealed class UnlinkProviderEndpoint(PlatformAuthentication authentication, HostedAuth hosted, Passcodes passcodes, Users users) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Delete("/v1/terrascale/account/providers/{provider}");
        this.Describe("account_unlink_provider", "Unlink my sign-in provider", "Requires a live sign-in token and a fresh emailed sign-in code. Email-code sign-in remains available. Success revokes every prior session and token; sign in again.",
            "Account", typeof(UnlinkProviderRequest), StatusCodes.Status204NoContent, null, scoped: false);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var provider = this.HttpContext.GetRouteValue("provider") as string;
        if (provider is not (SocialProviders.GitHub or SocialProviders.Google))
        {
            return ErrorResults.ToResult(new AuthValidation("Unknown provider."));
        }

        var body = await this.BodyAsync(PlatformSocialJson.Default.UnlinkProviderRequest, cancellationToken);
        if ((await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken)
            ?? await hosted.LimitCodeAsync(caller.Email, PasscodePurpose.SignIn, cancellationToken: cancellationToken)) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        if (await passcodes.RedeemAsync(caller.InstanceId, caller.Email, body.Code ?? string.Empty, PasscodePurpose.SignIn, cancellationToken: cancellationToken) is not { } proof
            || proof.Id != caller.Id || proof.AuthStamp != caller.AuthStamp || proof.AuthEpoch != caller.AuthEpoch
            || !await users.UnlinkProviderAsync(proof, provider, cancellationToken))
        {
            return ErrorResults.ToResult(new AuthUnauthorized("A current sign-in code is required.", "invalid_credentials"));
        }

        return TypedResults.NoContent();
    }
}
