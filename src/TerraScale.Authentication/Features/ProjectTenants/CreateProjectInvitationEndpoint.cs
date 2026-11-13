using System.Diagnostics;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class CreateProjectInvitationEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, ProjectInvitations invitations) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/invitations");
        this.Describe("create_project_invitation", "Invite an end user", "Needs the admin project role.", "Project tenants", typeof(CreateProjectInvitationRequest), StatusCodes.Status201Created, typeof(ProjectInvitationBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ProjectTenantJson.Default.CreateProjectInvitationRequest, cancellationToken);
        var invitation = await invitations.CreateAsync(projectId, request.Email, request.RedirectUrl, caller switch { ProjectCaller.SignedIn(var person) => person.Email, ProjectCaller.SecretKey(var key) => key.Name, _ => throw new UnreachableException() }, AuthEmails.Locale(this.HttpContext.Request), cancellationToken);
        return Results.Json(invitation, ProjectTenantJson.Default.ProjectInvitationBody, statusCode: 201);
    }
}
