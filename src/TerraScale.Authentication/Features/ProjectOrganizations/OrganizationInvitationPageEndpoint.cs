using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class OrganizationInvitationPageEndpoint(ProjectOrganizationDirectory organizations, Sessions sessions) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/ui/{slug}/organization-invitations");
        Name("ui_project_organization_invitation");
        Summary("Accept an organization invitation");
        Tags("Sign-in");
        Produces<string>(200, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var tenant = Tenancy.Resolved(this.HttpContext);
        if (tenant is null || !Tenancy.IsProject(tenant))
        {
            return Results.NotFound();
        }

        var secret = this.HttpContext.Request.Query["token"].ToString();
        var invite = await organizations.InvitationAsync(tenant, secret, cancellationToken);
        if (await sessions.ResolveAsync(tenant, this.HttpContext.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken) is null)
        {
            return Results.Redirect($"/ui/{tenant}/sign-in?next={Uri.EscapeDataString(this.HttpContext.Request.Path + this.HttpContext.Request.QueryString)}");
        }

        var org = await organizations.ReadAsync(tenant, invite.Value.OrganizationId, cancellationToken);
        return OrganizationPageModel.Render(this.HttpContext, new(tenant, "", $"Join {org.Value.Name}", invite.Value.Email, secret));
    }
}
