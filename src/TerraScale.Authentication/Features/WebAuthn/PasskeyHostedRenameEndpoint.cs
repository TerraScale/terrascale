using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyHostedRenameEndpoint(Sessions sessions, PasskeyCredentials passkeys, TotpCredentials mfa, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/security/passkeys/rename");
        Name("ui_security_passkey_rename");
        Summary("Rename a passkey from the security page");
        Description("Same-origin form post with id, name and next from the hosted security page's live session; renames the passkey exactly like rename_passkey (1 to 64 characters) and shows the security page again. A cross-origin post or a missing session answers 403 passkey_denied; an unknown passkey or a changed account shows the page with an error.");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
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

        var name = (this.HttpContext.Request.Form["name"].ToString() ?? string.Empty).Trim();
        if (name.Length is < 1 or > 64)
        {
            return await SecurityFlow.RefusedAsync(this.HttpContext, sessions, mfa, live.User, ("Passkey not renamed", "The name must be 1 to 64 characters."), StatusCodes.Status422UnprocessableEntity, cancellationToken);
        }

        var id = this.HttpContext.Request.Form["id"].ToString();
        if ((await passkeys.ListAsync(live.User, cancellationToken).ConfigureAwait(false)).All(entry => entry.CredentialId != id))
        {
            return await SecurityFlow.RefusedAsync(this.HttpContext, sessions, mfa, live.User, ("Passkey not renamed", "Reload the page and try again."), StatusCodes.Status409Conflict, cancellationToken);
        }

        var entry = await passkeys.RenameAsync(live.User, id, name, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new ApiException(403, "passkey_denied", "Sign in again before renaming a passkey.");
        }

        return await SecurityFlow.PageAsync(this.HttpContext, mfa, live.User, null, cancellationToken,
            notice: "Passkey renamed.", next: SignInFlow.SafeNext(this.HttpContext, form.Next));
    }
}
