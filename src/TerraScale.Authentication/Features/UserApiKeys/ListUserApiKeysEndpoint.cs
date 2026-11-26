using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class ListUserApiKeysEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserApiKeys keys) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Get(Root + "/users/{user}/api-keys");
        this.Describe("list_user_api_keys", "List user api keys", "Project-scoped user-owned keys; limit is 1 to 100 (default 100) and after the previous page's next. Requires the viewer role or the corresponding project key scope.", "User API keys", null, 200, typeof(UserApiKeyPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        _ = await this.Tenants.UserAsync(project, this.RouteId("user"), cancellationToken);
        var (limit, after) = this.Paging(100);
        return TypedResults.Json(await keys.ListAsync(project, this.RouteId("user"), limit, after, cancellationToken), UserApiKeyJson.Default.UserApiKeyPageBody);
    }
}
