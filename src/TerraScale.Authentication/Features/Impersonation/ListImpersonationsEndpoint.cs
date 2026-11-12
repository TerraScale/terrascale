using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed class ListImpersonationsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, Impersonations impersonations)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/impersonations");
        this.Describe("list_impersonations", "List active impersonations",
            "Requires a person with the project admin role. Tickets last 60 seconds and bind one client; starting requires a start_impersonation step-up proof.",
            "Impersonation", null, 200, typeof(ImpersonationPage));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        _ = caller.RequirePerson();
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return Results.Json(new ImpersonationPage(await impersonations.ListAsync(project, cancellationToken)), ImpersonationJson.Default.ImpersonationPage);
    }
}
