using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class ListUserApiKeysEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserApiKeys keys) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/users/{user}/api-keys");
        this.Describe("list_user_api_keys", "List user api keys", "Project-scoped user-owned keys. Requires the viewer role or the corresponding project key scope.", "User API keys", null, 200, typeof(UserApiKeyPageBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        _ = await this.Tenants.UserAsync(project, this.RouteId("user"), cancellationToken);
        return Results.Json(await keys.ListAsync(project, this.RouteId("user"), cancellationToken), UserApiKeyJson.Default.UserApiKeyPageBody);
    }
}
