using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class SetUserApiKeyPolicyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserApiKeys keys) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Put(Root + "/user-api-key-policy");
        this.Describe("set_user_api_key_policy", "Set user api key policy", "Project-scoped user-owned keys. Requires the admin role or the corresponding project key scope.", "User API keys", typeof(UserApiKeyPolicyBody), 200, typeof(UserApiKeyPolicyBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(UserApiKeyJson.Default.UserApiKeyPolicyBody, cancellationToken);
        return Results.Json(await keys.SetPolicyAsync(project, request.AllowedScopes, caller.Id, cancellationToken), UserApiKeyJson.Default.UserApiKeyPolicyBody);
    }
}
