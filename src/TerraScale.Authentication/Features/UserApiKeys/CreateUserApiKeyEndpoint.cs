using TerraScale.Authentication.Contracts;
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

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(UserApiKeyJson.Default.UserApiKeyRequest, cancellationToken);
        return Results.Json(await keys.CreateAsync(project, this.RouteId("user"), caller.Id, request, cancellationToken), UserApiKeyJson.Default.UserApiKeyBody, statusCode: 201);
    }
}
