using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Features.Social;

/// <summary>Emails the caller a sign-in code for unlinking one of its linked providers.</summary>
public sealed class SendUnlinkCodeEndpoint(PlatformAuthentication authentication, HostedAuth hosted, SignInMail mail) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/account/providers/{provider}/code");
        this.Describe("account_send_unlink_code", "Email me a code to unlink a provider", "Requires a live sign-in token and a linked provider. Emails a sign-in code to the account's verified email, subject to the sign-in mail limits; use it with DELETE /v1/terrascale/account/providers/{provider}.",
            "Account", null, StatusCodes.Status202Accepted, null, roleChecked: false, scoped: false);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var provider = this.HttpContext.GetRouteValue("provider") as string;
        if (provider is not (SocialProviders.GitHub or SocialProviders.Google)
            || !caller.LinkedProviders.Any(identity => identity.Provider == provider))
        {
            throw Invalid("Unknown provider.");
        }

        using var reservation = mail.Reserve();
        if (await hosted.LimitMailAsync(caller.Email, SignInFlow.Ip(this.HttpContext), cancellationToken) is { } refusal)
        {
            return HostedAuth.TooMany(refusal.Wait);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await reservation.EnqueueCodeAsync(caller.InstanceId, caller.Email, PasscodePurpose.SignIn, AuthEmails.Locale(this.HttpContext.Request.Headers.AcceptLanguage));

        return Results.Accepted();
    }
}
