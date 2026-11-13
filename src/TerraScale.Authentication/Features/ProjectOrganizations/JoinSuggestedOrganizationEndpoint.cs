using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class JoinSuggestedOrganizationEndpoint(ProjectOrganizationDirectory organizations, Sessions sessions, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/organizations/{org}/join");
        Name("ui_join_project_organization");
        Summary("Join or dismiss a suggested organization");
        Tags("Sign-in");
        Produces<string>(302, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!this.HttpContext.Request.HasFormContentType || this.HttpContext.Request.Headers.Origin is not [{ } origin]
            || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
        {
            return Results.StatusCode(403);
        }

        var tenant = Tenancy.Resolved(this.HttpContext);
        if (tenant is null || !Tenancy.IsProject(tenant)
            || await sessions.ResolveAsync(tenant, this.HttpContext.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken) is not { } live)
        {
            return Results.Unauthorized();
        }

        var form = await this.HttpContext.Request.ReadFormAsync(cancellationToken);
        var next = SignInFlow.SafeNext(this.HttpContext, form["next"]);
        var org = this.HttpContext.GetRouteValue("org") as string ?? throw PlatformDirectory.NotFound();
        if (form["dismiss"] == "true")
        {
            await organizations.DismissSuggestionAsync(live.User, org, cancellationToken);
        }
        else
        {
            await organizations.JoinSuggestedAsync(live.User, org, cancellationToken);
        }
        return Results.Redirect(next ?? $"/ui/{tenant}/sign-in");
    }
}
