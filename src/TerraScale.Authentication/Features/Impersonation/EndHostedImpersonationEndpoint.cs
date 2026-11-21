using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed class EndHostedImpersonationEndpoint(Impersonations impersonations, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/impersonation/end");
        Name("end_hosted_impersonation");
        Summary("End the current impersonation");
        Description("Same-origin form post; ends the impersonation and clears its isolated browser cookie.");
        Tags("Impersonation");
        Produces(302);
        Produces(403);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(this.HttpContext.Request.Headers.Origin, UriKind.Absolute, out var origin)
            || origin.GetLeftPart(UriPartial.Authority) != options.PublicOrigin.GetLeftPart(UriPartial.Authority))
        {
            return Results.StatusCode(403);
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (await impersonations.ResolveAsync(tenant, this.HttpContext.Request.Cookies[Impersonations.Cookie(tenant)], cancellationToken) is { } live)
        {
            await impersonations.EndAsync(tenant, live.Record.Id, live.Record.ActorId, "manual", cancellationToken);
        }

        this.HttpContext.Response.Cookies.Delete(Impersonations.Cookie(tenant), ImpersonationBrowser.CookieOptions(null));
        return Results.Redirect($"/ui/{tenant}/sign-in");
    }
}
