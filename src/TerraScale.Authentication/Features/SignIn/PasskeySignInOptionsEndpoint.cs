using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class PasskeySignInOptionsEndpoint(PasskeyCredentials passkeys, HostedAuth hosted, TotpCredentials mfa, Sessions sessions, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/passkey/options");
        Name("ui_sign_in_passkey_options");
        Summary("Start a passkey sign-in");
        Description("Same-origin form post with the email address and, on the second-factor step, its challenge. Returns WebAuthn PublicKeyCredentialRequestOptions with a sealed single-use challenge valid 5 minutes and userVerification required. allowCredentials stays empty, so the authenticator offers its discoverable passkeys and the answer reveals nothing about which addresses have accounts or passkeys, unless the caller already identified the account: a live second-factor challenge or a live session for that address lists the account's own credentials, so passkeys enrolled without a resident key keep working there. Pass the answer to ui_sign_in_passkey.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<PasskeyOptionsBody>(StatusCodes.Status200OK);
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many sign-in attempts; retry after the Retry-After delay.", wait);
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        var (assertion, challenge) = await passkeys.SignInOptionsAsync(tenant, await this.IdentifiedAsync(tenant, form, cancellationToken), cancellationToken);
        return TypedResults.Json(new PasskeyOptionsBody(challenge, JsonDocument.Parse(assertion.ToJson()).RootElement), WebAuthnJson.Default.PasskeyOptionsBody);
    }

    private async Task<UserRecord?> IdentifiedAsync(string tenant, SignInForm form, CancellationToken cancellationToken)
    {
        if (form.Challenge is { Length: > 0 } sealedChallenge)
        {
            return (await mfa.ChallengedAsync(tenant, sealedChallenge, "verification", cancellationToken))?.User;
        }

        return Users.NormalizeEmail(form.Email) is { } email
            && await sessions.ResolveAsync(tenant, this.HttpContext.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken) is { User: var user }
            && Users.NormalizeEmail(user.Email) == email
                ? user : null;
    }
}
