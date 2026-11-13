using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Sso;

public sealed class SsoLinkEndpoint(SsoSignIn sso, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(SsoSignIn.LinkPath);
        Name("ui_sso_link");
        Summary("Confirm linking a single sign-on identity");
        Description("Confirms, for the browser's pending single sign-on continuation of an existing unlinked account, that the identity provider's sign-in is linked to the account once its ordinary sign-in completes. Requires a same-origin form post, a confirmation digest from the displayed continuation and the tsauth_sso_proof cookie; answers the password page. A stale form displays the current confirmation without changing the continuation. A continuation that is missing, expired, already linked or already confirmed answers 403.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!this.HttpContext.Request.HasFormContentType
            || this.HttpContext.Request.Headers.Origin is not [{ } origin] || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var form = await this.HttpContext.Request.ReadFormAsync(cancellationToken);
        return sso.Confirm(this.HttpContext, form["confirmation"]) is { } proof
            ? SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Password, PlatformDirectory.Instance, proof.Email, proof.Next))
            : await sso.LinkPromptAsync(this.HttpContext, cancellationToken);
    }
}
