using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyHostedDeleteEndpoint(Sessions sessions, PasskeyCredentials passkeys, TotpCredentials mfa, PlatformDirectory platform, AuthOptions options,
    ILogger<PasskeyHostedDeleteEndpoint> logger) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/security/passkeys/delete");
        Name("ui_security_passkey_delete");
        Summary("Remove a passkey from the security page");
        Description("Same-origin form post with id, code and next from the hosted security page's live session; the code (the authenticator's current code or an unused recovery code) reauthenticates the removal exactly as turning off two-factor authentication does. Success removes the passkey, releases its credential claim and records member.passkey_removed in the account's organizations' audit logs; sessions and tokens stay. Removing the last passkey of an account without an authenticator app that belongs to an organization requiring two-factor authentication is refused. A wrong code answers 401; a cross-origin post answers 403.");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status409Conflict, "text/html");
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

        if (await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live)
        {
            return SecurityFlow.SignIn(this.HttpContext);
        }

        var id = this.HttpContext.Request.Form["id"].ToString();
        var entries = await passkeys.ListAsync(live.User, cancellationToken).ConfigureAwait(false);
        if (entries.All(entry => entry.CredentialId != id))
        {
            return await SecurityFlow.RefusedAsync(this.HttpContext, sessions, mfa, live.User, ("Passkey not removed", "Reload the page and try again."), StatusCodes.Status409Conflict, cancellationToken);
        }

        if (await PasskeyGuard.LastFactorAsync(platform, mfa, live.User.Id, entries.Count - 1, cancellationToken).ConfigureAwait(false))
        {
            return await SecurityFlow.RefusedAsync(this.HttpContext, sessions, mfa, live.User, ("Last passkey", "An organization you belong to requires two-factor authentication; add another passkey or an authenticator app first."), StatusCodes.Status409Conflict, cancellationToken);
        }

        var code = (form.Code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (!await mfa.VerifyAsync(live.User, code, cancellationToken).ConfigureAwait(false))
        {
            return await SecurityFlow.RefusedAsync(this.HttpContext, sessions, mfa, live.User, ("That code is wrong or already used", "Enter the current code from your authenticator app, or an unused recovery code."), StatusCodes.Status401Unauthorized, cancellationToken);
        }

        if (await passkeys.DeleteAsync(live.User, id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ApiException(403, "passkey_denied", "Sign in again before removing a passkey.");
        }

        logger.ZLogInformation($"Audit: user {live.User.Id:@UserId} removed a passkey");
        await SecurityFlow.AuditAsync(platform, SignInFlow.Tenant(this.HttpContext), live.User.Id, "member.passkey_removed", cancellationToken);
        return await SecurityFlow.PageAsync(this.HttpContext, mfa, live.User, null, cancellationToken,
            notice: "Passkey removed.", next: SignInFlow.SafeNext(this.HttpContext, form.Next));
    }
}
