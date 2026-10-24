using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Features.Social;

/// <summary>Fresh email-code proof before unlinking a provider.</summary>
/// <param name="Code">A current sign-in code sent to the account's verified email.</param>
public sealed record UnlinkProviderRequest([property: JsonPropertyName("code")] string? Code);

/// <summary>Unlinks a provider after reauthentication, retaining email-code recovery.</summary>
public sealed class UnlinkProviderEndpoint(PlatformAuthentication authentication, HostedAuth hosted, Passcodes passcodes, Users users) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/v1/terrascale/account/providers/{provider}");
        this.Document("account_unlink_provider", "Unlink my sign-in provider", "Requires a live sign-in token and a fresh emailed sign-in code. Email-code sign-in remains available. Success revokes every prior session and token; sign in again.",
            "Account", typeof(UnlinkProviderRequest), StatusCodes.Status204NoContent, null, roleChecked: false, scoped: false);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var provider = this.HttpContext.GetRouteValue("provider") as string;
        if (provider is not (SocialProviders.GitHub or SocialProviders.Google))
        {
            throw Invalid("Unknown provider.");
        }

        var body = await this.BodyAsync(SocialJson.Default.UnlinkProviderRequest, cancellationToken);
        if ((await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken)
            ?? await hosted.LimitCodeAsync(caller.Email, PasscodePurpose.SignIn, issuing: false, cancellationToken)) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        if (await passcodes.RedeemAsync(caller.InstanceId, caller.Email, body.Code ?? string.Empty, PasscodePurpose.SignIn, cancellationToken) is not { } proof
            || proof.Id != caller.Id || proof.AuthStamp != caller.AuthStamp || proof.AuthEpoch != caller.AuthEpoch
            || !await users.UnlinkProviderAsync(proof, provider, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials", "A current sign-in code is required.");
        }

        return Results.NoContent();
    }
}
