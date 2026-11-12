using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed class ConfirmUnlockAccountEndpoint(AccountProtection protection, AttemptLimiter limiter, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/{project}/attack-protection/unlock");
        Name("confirm_unlock_project_account");
        Summary("Redeem a single-use account unlock link");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/plain");
        Produces(StatusCodes.Status403Forbidden);
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

        this.HttpContext.Response.Headers.CacheControl = "no-store";
        this.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!this.HttpContext.Request.HasFormContentType || this.HttpContext.Request.Headers.Origin is not [{ } origin]
            || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var form = await this.HttpContext.Request.ReadFormAsync(cancellationToken);
        await protection.UnlockAsync(this.HttpContext.GetRouteValue("project") as string ?? "",
            form["user"].ToString(), form["token"].ToString(), cancellationToken);
        return Results.Text("If the unlock link was valid, password sign-in is available again.");
    }
}
