using System.Diagnostics;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ApproveProjectWaitlistEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, ProjectInvitations invitations) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/waitlist/{entry}/approve");
        this.Describe("approve_project_waitlist", "Approve a waitlist entry", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        await invitations.DecideAsync(projectId, this.RouteId("entry"), true, caller switch { ProjectCaller.SignedIn(var person) => person.Email, ProjectCaller.SecretKey(var key) => key.Name, _ => throw new UnreachableException() }, AuthEmails.Locale(this.HttpContext.Request), cancellationToken);
        return Results.NoContent();
    }
}
