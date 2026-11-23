using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class UpdateProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserProfiles profiles)
    : ProjectTenantEndpoint(tenants, authentication)
{
    protected override int MaximumBodyBytes => 32 * 1024;

    public override void Configure()
    {
        Patch(Root + "/users/{user}");
        this.Describe("update_project_user", "Update an end user", "Requires auth:write or the admin project role. Omitted fields are unchanged. Metadata uses JSON merge patch; each merged object is at most 8192 UTF-8 bytes and depth 10. custom_claims replaces the trusted claims whole and null clears them: an object of at most 32 members and 1000 bytes serialized, names matching [a-z][a-z0-9_]{0,62}, values strings of at most 256 characters, finite numbers, booleans, or arrays of at most 64 strings or 64 numbers. Every later access token of the user carries them as claims. Unchanged patches emit no event.",
            "Project tenants", typeof(UserProfilePatch), StatusCodes.Status200OK, typeof(ProjectUserBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var patch = await this.BodyAsync(UserProfileJson.Default.UserProfilePatch, cancellationToken);
        return TypedResults.Json(Map(await profiles.UpdateAsync(project, this.RouteId("user"), patch, cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
