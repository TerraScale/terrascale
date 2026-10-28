using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Sso;

/// <summary><c>GET /ui/terrascale/sso</c>: sends the browser to the identity provider of the email's organization.</summary>
public sealed class SsoStartEndpoint(SsoSignIn sso, HostedAuth hosted) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(SsoSignIn.StartPath);
        Name("ui_sso_start");
        Summary("Start a single sign-on");
        Description("With email (and next, honoured as on the sign-in page), redirects to the OpenID Connect identity provider of the organization that verified the email's domain, with state, nonce and PKCE (S256) sealed into the tsauth_sso cookie for 10 minutes. 400 when the domain has no verified connection; shares the sign-in IP call bucket (120, refilling 30 per minute).");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status302Found);
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var email = this.HttpContext.Request.Query["email"].ToString().Trim();
        var next = SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]);
        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait)
        {
            return SignInFlow.Limited(this.HttpContext, SignInStep.Email, wait, email, next);
        }

        if (await sso.StartAsync(email, next, cancellationToken) is not { } start)
        {
            AuthMetrics.SignIn("hosted_sso", "refused");
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Email, SignInFlow.Tenant(this.HttpContext), email, next,
                "This email's domain has no single sign-on, or its identity provider cannot be reached.")
            { ErrorTitle = "Single sign-on unavailable" }, StatusCodes.Status400BadRequest);
        }

        this.HttpContext.Response.Cookies.Append(SsoSignIn.Cookie, start.State, new CookieOptions
        {
            Path = SsoSignIn.StartPath,
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = SsoSignIn.Lifetime,
        });
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        return Results.Redirect(start.Location);
    }
}
