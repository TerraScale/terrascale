using TerraScale.Authentication.Contracts;
using TerraScale.DotNet;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class VerifyUserApiKeyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserApiKeys keys) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/api-keys/verify");
        this.Describe("verify_user_api_key", "Verify user api key", "Project-scoped user-owned keys. Requires an unrestricted project secret API key with auth:read; sign-in tokens are refused. Reads current storage without caching and compares hashes in constant time.", "User API keys", typeof(VerifyUserApiKeyRequest), 200, typeof(VerifyUserApiKeyBody));
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        if (caller is not ProjectCaller.SecretKey)
        {
            throw PlatformDirectory.Forbidden();
        }

        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken, TerraBaseScopes.AuthRead);
        var request = await this.BodyAsync(UserApiKeyJson.Default.VerifyUserApiKeyRequest, cancellationToken);
        return Results.Json(await keys.VerifyAsync(project, request.Key, cancellationToken), UserApiKeyJson.Default.VerifyUserApiKeyBody);
    }
}
