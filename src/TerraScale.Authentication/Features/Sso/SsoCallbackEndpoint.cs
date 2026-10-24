using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Sso;

/// <summary><c>GET /ui/terrascale/sso/callback</c>: the identity provider's redirect; opens a session and resumes.</summary>
public sealed class SsoCallbackEndpoint(SsoSignIn sso, Sessions sessions, HostedAuth hosted) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(SsoSignIn.CallbackPath);
        Name("ui_sso_callback");
        Summary("Finish a single sign-on");
        Description("The OpenID Connect redirect URI. The state must match the browser's tsauth_sso cookie, which is cleared. The code is redeemed with PKCE and the client secret; the ID token must be signed by the issuer's keys, for the client, unexpired, carry the nonce and an email in the verified domain (email_verified, when sent, true). A first sign-in creates the account and joins the organization with the connection's default role. Success sets the tsauth_session cookie and continues to next; any failure answers 401.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var query = this.HttpContext.Request.Query;
        var pending = sso.Pending(this.HttpContext.Request.Cookies[SsoSignIn.Cookie], query["state"]);
        this.HttpContext.Response.Cookies.Delete(SsoSignIn.Cookie, new CookieOptions { Path = SsoSignIn.StartPath, Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        if (pending is null)
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Rejected, SignInFlow.Tenant(this.HttpContext)), StatusCodes.Status403Forbidden);
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait)
        {
            AuthMetrics.SignIn("hosted_sso", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.Email, wait, string.Empty, pending.Next);
        }

        if (query["code"].ToString() is not { Length: > 0 and <= 4096 } code
            || await sso.CompleteAsync(pending, code, cancellationToken) is not { } user
            || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_sso", "refused");
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Email, SignInFlow.Tenant(this.HttpContext), Next: pending.Next,
                Error: "Your identity provider did not complete the sign-in. Try again, or ask your administrator.")
            { ErrorTitle = "Single sign-on failed" }, StatusCodes.Status401Unauthorized);
        }

        AuthMetrics.SignIn("hosted_sso", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, pending.Next, cancellationToken, viaSso: true);
    }
}
