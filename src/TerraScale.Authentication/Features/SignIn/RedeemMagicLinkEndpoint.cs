using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Security;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class RedeemMagicLinkEndpoint(Passcodes passcodes, Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/ui/{slug}/sign-in/magic");
        Name("ui_sign_in_magic_redeem");
        Summary("Redeem a browser-bound sign-in link");
        Description("A single-use ten-minute email link. A different browser receives the email-code form without redeeming the link. Shares sign-in attempt budgets and requires the local second factor when configured.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var context = this.HttpContext;
        var tenant = SignInFlow.Tenant(context);
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (SignInFlow.WrongSlug(context) || !HostedTheme.Of(context).MagicLink)
        {
            return Results.NotFound();
        }

        if (MagicLinks.Open(options, tenant, context.Request.Query["token"], clock) is not { } proof)
        {
            return SocialFlow.Failed(context, null, StatusCodes.Status401Unauthorized, "This sign-in link expired. Request a new link.");
        }

        var next = SignInFlow.SafeNext(context, proof.Next);
        var sameBrowser = context.Request.Cookies[MagicLinks.Cookie(tenant)] is { } browser
            && Tokens.FixedTimeEquals(proof.BrowserHash, Tokens.Sha256Hex(browser));
        if (await hosted.LimitIpAsync(SignInFlow.Ip(context), mails: false, cancellationToken) is { } wait)
        {
            return sameBrowser
                ? SignInFlow.Limited(context, SignInStep.Code, wait, proof.Email, next)
                : MagicLinks.CodePage(context, proof, context.Request.Query["token"]!, "Too many attempts. Try again later.", StatusCodes.Status429TooManyRequests);
        }

        if (!sameBrowser)
        {
            return MagicLinks.CodePage(context, proof, context.Request.Query["token"]!,
                "Open this link in the browser that requested it, or enter the code from the email.");
        }

        if (await hosted.LimitCodeAsync(proof.Email, PasscodePurpose.SignIn, cancellationToken) is { } codeWait)
        {
            return SignInFlow.Limited(context, SignInStep.Code, codeWait, proof.Email, next);
        }

        var user = await passcodes.RedeemAsync(tenant, proof.Email, proof.Code, PasscodePurpose.SignIn, proof.IssueId, cancellationToken);
        if (user is not null)
        {
            context.Response.Cookies.Delete(MagicLinks.Cookie(tenant), MagicLinks.CookieOptions(tenant));
        }

        if (user is not null && await SignInFlow.SecondFactorAsync(context, mfa, user, next, cancellationToken) is { } factor)
        {
            return factor;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            return SignInFlow.WrongCode(context, SignInStep.Code, proof.Email, next);
        }

        return await SignInFlow.SignedInAsync(context, sessions, session, next, cancellationToken);
    }
}
