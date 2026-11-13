using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using ZLogger;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class SetProjectProviderEndpoint(TenantDirectory tenants, ProjectAuthSettings settings, AuthOptions options, PlatformAuthentication authentication,
    ILogger<SetProjectProviderEndpoint> logger) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Put(Root + "/providers/{provider}");
        this.Describe("set_project_provider", "Configure a social sign-in provider",
            "provider is google, github, apple, microsoft, gitlab, linkedin, slack, discord, facebook, x, oidc or oauth2. The project's own OAuth client: client_id and client_secret (Apple: the Services ID and the .p8 private key, with team_id and key_id; oidc: also the https issuer). microsoft accepts tenant common (default), organizations or consumers; oauth2 requires authorize_url, token_url, userinfo_url, scopes, id_claim, email_claim and email_verified_claim in oauth2, with HTTPS endpoints and dotted claim paths. client_secret is stored encrypted and never answered; omitted, the stored one is kept. Register callback_url at the provider. Needs a person with the admin project role because this changes the external sign-in credentials; project API keys are refused.",
            "Project tenants", typeof(SetProviderRequest), StatusCodes.Status200OK, typeof(SettingsBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _ = caller.RequirePerson();
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var provider = this.HttpContext.GetRouteValue("provider") is string name && SocialProviders.Names.Contains(name) ? name : throw PlatformDirectory.NotFound();

        var request = await this.BodyAsync(ProjectTenantJson.Default.SetProviderRequest, cancellationToken);
        var updated = await settings.UpdateAsync(projectId, current =>
        {
            var record = SocialProviders.Seal(options, projectId, provider, current.Provider(provider), request.ClientId, request.ClientSecret, request.Issuer, request.TeamId, request.KeyId, request.Tenant, request.OAuth2);
            return current with { Providers = [.. (current.Providers ?? []).Where(other => other.Provider != provider), record] };
        }, cancellationToken);
        logger.ZLogInformation($"Audit: user {caller.Id:@UserId} configured the {provider:@Provider} sign-in provider of project {projectId:@ProjectId}");
        return Results.Json(await this.SettingsAsync(projectId, updated, cancellationToken), ProjectTenantJson.Default.SettingsBody);
    }
}
