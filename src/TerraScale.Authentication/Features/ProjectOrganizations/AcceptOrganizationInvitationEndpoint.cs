using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class AcceptOrganizationInvitationEndpoint(ProjectOrganizationDirectory organizations, Sessions sessions, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/organization-invitations");
        Name("ui_accept_project_organization_invitation");
        Summary("Accept an organization invitation");
        Tags("Sign-in");
        Produces<string>(200, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!this.HttpContext.Request.HasFormContentType || this.HttpContext.Request.Headers.Origin is not [{ } origin] || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
        {
            return Results.StatusCode(403);
        }

        var tenant = Tenancy.Resolved(this.HttpContext);
        if (tenant is null || !Tenancy.IsProject(tenant)
            || await sessions.ResolveAsync(tenant, this.HttpContext.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken) is not { } live)
        {
            return Results.Unauthorized();
        }

        var org = await organizations.AcceptAsync(live.User, (await this.HttpContext.Request.ReadFormAsync(cancellationToken))["token"].ToString(), cancellationToken);
        return OrganizationPageModel.Render(this.HttpContext, new(tenant, "", org.Slug, Done: "Invitation accepted"));
    }
}
