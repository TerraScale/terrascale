using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed class CreateUserApiKeyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserApiKeys keys) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/users/{user}/api-keys");
        this.Describe("create_user_api_key", "Create user api key", "Project-scoped user-owned keys. Requires the admin role or the corresponding project key scope.", "User API keys", typeof(UserApiKeyRequest), 201, typeof(UserApiKeyBody), conflict: true);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(UserApiKeyJson.Default.UserApiKeyRequest, cancellationToken);
        return await keys.CreateAsync(project, this.RouteId("user"), caller.Id, request, null, cancellationToken) switch
        {
            UserApiKeyBody key => Results.Json(key, UserApiKeyJson.Default.UserApiKeyBody, statusCode: 201),
            Validation error => ErrorResults.ToResult(error),
            NotFound error => ErrorResults.ToResult(error),
            Forbidden error => ErrorResults.ToResult(error),
            QuotaExceeded error => ErrorResults.ToResult(error),
            Conflict error => ErrorResults.ToResult(error),
        };
    }
}
