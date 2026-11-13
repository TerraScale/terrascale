using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class GetUserApiKeyPolicyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserApiKeys keys) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/user-api-key-policy");
        this.Describe("get_user_api_key_policy", "Get user api key policy", "Project-scoped user-owned keys. Requires the viewer role or the corresponding project key scope.", "User API keys", null, 200, typeof(UserApiKeyPolicyBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return Results.Json(await keys.PolicyAsync(project, cancellationToken), UserApiKeyJson.Default.UserApiKeyPolicyBody);
    }
}
