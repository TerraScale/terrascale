using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class RevokeUserApiKeyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserApiKeys keys) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Delete(Root + "/users/{user}/api-keys/{key}");
        this.Describe("revoke_user_api_key", "Revoke user api key", "Project-scoped user-owned keys. Requires the admin role or the corresponding project key scope.", "User API keys", null, 204, null, conflict: true);
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return await keys.RevokeAsync(project, this.RouteId("user"), this.RouteId("key"), caller.Id, cancellationToken) switch
        {
            Done => Results.NoContent(),
            NotFound error => ErrorResults.ToResult(error),
            Conflict error => ErrorResults.ToResult(error),
        };
    }
}
