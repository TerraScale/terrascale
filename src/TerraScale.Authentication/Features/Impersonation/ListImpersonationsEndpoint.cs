using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed class ListImpersonationsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, Impersonations impersonations)
    : ProjectTenantEndpoint(tenants, authentication)
{
    protected override bool PersonOnly => true;

    public override void Configure()
    {
        AuthenticateInHandler();
        Get(Root + "/impersonations");
        this.Describe("list_impersonations", "List active impersonations",
            "Requires a person with the project admin role. Returns up to 100 active impersonations in the project.",
            "Impersonation", null, 200, typeof(ImpersonationPage));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        _ = caller.RequirePerson();
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return TypedResults.Json(new ImpersonationPage(await impersonations.ListAsync(project, cancellationToken)), ImpersonationJson.Default.ImpersonationPage);
    }
}
