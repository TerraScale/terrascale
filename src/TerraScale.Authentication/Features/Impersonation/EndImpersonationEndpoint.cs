using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed class EndImpersonationEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, Impersonations impersonations)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/impersonations/{impersonation}/end");
        this.Describe("end_impersonation", "End an impersonation",
            "Requires a person with the project admin role. Tickets last 60 seconds and bind one client; starting requires a start_impersonation step-up proof.",
            "Impersonation", null, 204, null);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        _ = caller.RequirePerson();
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        await impersonations.EndAsync(project, this.RouteId("impersonation"), caller.Id, cancellationToken);
        return Results.NoContent();
    }
}
