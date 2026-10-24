using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using ZLogger;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>DELETE /v1/{project}/providers/{provider}</c>.</summary>
public sealed class DeleteProjectProviderEndpoint(TenantDirectory tenants, ProjectAuthSettings settings, PlatformAuthentication authentication,
    ILogger<DeleteProjectProviderEndpoint> logger) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(Root + "/providers/{provider}");
        this.Document("delete_project_provider", "Remove a social sign-in provider",
            "Removes the provider and its stored secret; its button leaves the hosted pages. Users who signed in with it keep their accounts and can use the other methods. Needs the admin project role.",
            "Project tenants", null, StatusCodes.Status200OK, typeof(SettingsBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var provider = this.HttpContext.GetRouteValue("provider") is string name && SocialProviders.Names.Contains(name) ? name : throw PlatformDirectory.NotFound();

        var updated = await settings.UpdateAsync(projectId, current => current with { Providers = [.. (current.Providers ?? []).Where(other => other.Provider != provider)] }, cancellationToken);
        logger.ZLogInformation($"Audit: user {caller.Id:@UserId} removed the {provider:@Provider} sign-in provider of project {projectId:@ProjectId}");
        return Results.Json(await this.SettingsAsync(projectId, updated, cancellationToken), ProjectTenantJson.Default.SettingsBody);
    }
}
