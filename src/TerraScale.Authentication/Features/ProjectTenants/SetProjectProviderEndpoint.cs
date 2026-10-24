using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using ZLogger;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>PUT /v1/{project}/providers/{provider}</c>.</summary>
public sealed class SetProjectProviderEndpoint(TenantDirectory tenants, ProjectAuthSettings settings, AuthOptions options, PlatformAuthentication authentication,
    ILogger<SetProjectProviderEndpoint> logger) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(Root + "/providers/{provider}");
        this.Document("set_project_provider", "Configure a social sign-in provider",
            "provider is google, github, apple or oidc. The project's own OAuth client: client_id and client_secret (Apple: the Services ID and the .p8 private key, with team_id and key_id; oidc: also the https issuer). client_secret is stored encrypted and never answered; omitted, the stored one is kept. Register callback_url at the provider. Needs the admin project role.",
            "Project tenants", typeof(SetProviderRequest), StatusCodes.Status200OK, typeof(SettingsBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var provider = this.HttpContext.GetRouteValue("provider") is string name && SocialProviders.Names.Contains(name) ? name : throw PlatformDirectory.NotFound();

        var request = await this.BodyAsync(ProjectTenantJson.Default.SetProviderRequest, cancellationToken);
        var updated = await settings.UpdateAsync(projectId, current =>
        {
            var record = SocialProviders.Seal(options, projectId, provider, current.Provider(provider), request.ClientId, request.ClientSecret, request.Issuer, request.TeamId, request.KeyId);
            return current with { Providers = [.. (current.Providers ?? []).Where(other => other.Provider != provider), record] };
        }, cancellationToken);
        logger.ZLogInformation($"Audit: user {caller.Id:@UserId} configured the {provider:@Provider} sign-in provider of project {projectId:@ProjectId}");
        return Results.Json(await this.SettingsAsync(projectId, updated, cancellationToken), ProjectTenantJson.Default.SettingsBody);
    }
}
