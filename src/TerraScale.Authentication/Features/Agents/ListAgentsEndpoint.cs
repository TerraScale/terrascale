using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class ListAgentsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/agents");
        this.Describe("list_agents", "List agents", "owner filters by the owning user id; limit is 1 to 100 (default 50) and after the previous page's next. Agent identities. Requires the viewer role or the corresponding project key scope.", "Agents", null, 200, typeof(AgentPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(50);
        var owner = this.HttpContext.Request.Query["owner"].ToString();
        if (owner.Length != 0 && !PlatformDirectory.IsId(owner))
        {
            return ErrorResults.ToResult(new Validation("owner must be a user id."));
        }

        return TypedResults.Json(await agents.ListAsync(project, owner.Length == 0 ? null : owner, limit, after, cancellationToken), AgentJson.Default.AgentPageBody);
    }
}
