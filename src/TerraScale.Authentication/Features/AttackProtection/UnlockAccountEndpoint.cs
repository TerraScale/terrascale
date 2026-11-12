using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed class UnlockAccountEndpoint(AttemptLimiter limiter) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/{project}/attack-protection/unlock");
        Name("unlock_project_account");
        Summary("Confirm a single-use account unlock link");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await limiter.AcquireTokenAsync(OidcIssuer.InstanceId, "account-unlock-ip", limiter.Fold(SignInFlow.Ip(this.HttpContext)),
            30, TimeSpan.FromSeconds(2), cancellationToken) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(this.HttpContext.Response, nonce);
        this.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        return UnlockAccountPage.Create(new UnlockAccountPageModel(
            this.HttpContext.GetRouteValue("project") as string ?? "",
            this.HttpContext.Request.Query["user"].ToString(), this.HttpContext.Request.Query["token"].ToString(), nonce));
    }
}

public sealed record UnlockAccountPageModel(string Project, string User, string Token, string Nonce);
