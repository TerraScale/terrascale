using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Identity;
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
            "Requires a person with the project admin role. Returns up to 100 active impersonations in the project.",
            "Impersonation", null, 200, typeof(ImpersonationPage));
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        if (caller.RequirePerson() is not UserRecord)
        {
            return ErrorResults.ToResult(ProjectCaller.PersonRequired);
        }

        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return Results.Json(new ImpersonationPage(await impersonations.ListAsync(project, cancellationToken)), ImpersonationJson.Default.ImpersonationPage);
    }
}
