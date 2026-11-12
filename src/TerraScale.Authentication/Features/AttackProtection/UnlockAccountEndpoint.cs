using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed class UnlockAccountEndpoint(AccountProtection protection, AttemptLimiter limiter) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/{project}/attack-protection/unlock");
        Name("unlock_project_account");
        Summary("Redeem a single-use account unlock link");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/plain");
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

        var tenant = this.HttpContext.GetRouteValue("project") as string ?? "";
        await protection.UnlockAsync(tenant, this.HttpContext.Request.Query["user"].ToString(),
            this.HttpContext.Request.Query["token"].ToString(), cancellationToken);
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        this.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Text("If the unlock link was valid, password sign-in is available again.");
    }
}
