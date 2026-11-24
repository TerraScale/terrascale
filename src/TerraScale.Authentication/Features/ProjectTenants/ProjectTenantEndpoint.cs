using Dapper;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record MethodsBody(bool Passcode, bool Password, string SignUpMode, bool MagicLink = false, bool Username = false);

public sealed record BrandingBody(string? Name, string? LogoUrl, string? ThemeColor, int? TitleSize = null, string? DefaultTheme = null,
    IReadOnlyList<FooterLinkBody>? FooterLinks = null, string? BackgroundColor = null, string? TextColor = null, [property: JsonPropertyName("email_message")] string? EmailMessage = null);

public sealed record FooterLinkBody(string Label, string Url);

public sealed record ConfigurationBody(IReadOnlyList<string> RedirectUrls, int SessionLifetimeSeconds = 1209600,
    bool RequireMfa = false, bool AllowTotp = true, bool AllowPasskey = true, int? InactivityTimeoutMinutes = null, bool SingleSession = false)
{
    public static readonly ConfigurationBody Default = new([]);
}

public sealed record CountsBody(long Users, long Clients);

public sealed record ProviderBody(string Provider, bool Configured, string? ClientId, string? Issuer, string? TeamId, string? KeyId, string CallbackUrl, string? Tenant = null, [property: JsonPropertyName("oauth2")] OAuth2Configuration? OAuth2 = null);

public sealed record SettingsBody(string ProjectId, string Issuer, string DiscoveryUrl, string SignInUrl, MethodsBody Methods, BrandingBody Branding, CountsBody Counts,
    IReadOnlyList<ProviderBody> Providers, ConfigurationBody? Configuration = null, AttackProtectionStatus? AttackProtection = null, RestrictionsBody? Restrictions = null, bool OrganizationPicker = false);

public sealed record SetProviderRequest(string? ClientId, string? ClientSecret, string? Issuer = null, string? TeamId = null, string? KeyId = null, string? Tenant = null, [property: JsonPropertyName("oauth2")] OAuth2Configuration? OAuth2 = null);

public sealed record MethodsPatch(bool? Passcode, bool? Password, string? SignUpMode, bool? MagicLink = null, bool? Username = null);

public sealed record UpdateSettingsRequest(MethodsPatch? Methods, BrandingBody? Branding, ConfigurationBody? Configuration = null, AttackProtectionSettings? AttackProtection = null, RestrictionsBody? Restrictions = null, bool? OrganizationPicker = null);

public sealed record ProjectUserBody(string Id, string Email, DateTimeOffset CreatedAt, bool Disabled, string? FirstName, string? LastName,
    string? ImageUrl, string? ExternalId, bool EmailVerified, JsonElement PublicMetadata, JsonElement PrivateMetadata, JsonElement UnsafeMetadata,
    JsonElement? CustomClaims)
{
    public static ProjectUserBody From(UserRecord user) => new(user.Id, user.Email, user.CreatedAt, user.Disabled, user.FirstName, user.LastName,
        user.ImageUrl, user.ExternalId, user.EmailVerified, JsonElement.Parse(user.PublicMetadata), JsonElement.Parse(user.PrivateMetadata), JsonElement.Parse(user.UnsafeMetadata),
        user.CustomClaims is { } claims ? JsonElement.Parse(claims) : null);
}

public sealed record ProjectUserPageBody(IReadOnlyList<ProjectUserBody> Items, string? Next);

public sealed record ProjectSessionBody(string Id, string UserId, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record ProjectSessionPageBody(IReadOnlyList<ProjectSessionBody> Items, string? Next);

public sealed record ClientBody(string ClientId, string Name, string Type, IReadOnlyList<string> RedirectUris, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null, IReadOnlyList<string>? AllowedScopes = null, IReadOnlyList<string>? AllowedAudiences = null, int? TokenLifetimeMinutes = null,
    IReadOnlyList<string>? AllowedOrigins = null, bool AllowAnonymous = false);

public sealed record ClientPageBody(IReadOnlyList<ClientBody> Items, string? Next);

public sealed record CreateClientRequest(string? Name, string? Type, IReadOnlyList<string>? RedirectUris, IReadOnlyList<string>? AllowedScopes = null, IReadOnlyList<string>? AllowedAudiences = null, int? TokenLifetimeMinutes = null,
    IReadOnlyList<string>? AllowedOrigins = null, bool? AllowAnonymous = null);

public sealed record UpdateClientRequest(string? Name, IReadOnlyList<string>? RedirectUris, IReadOnlyList<string>? AllowedScopes = null, IReadOnlyList<string>? AllowedAudiences = null, int? TokenLifetimeMinutes = null,
    IReadOnlyList<string>? AllowedOrigins = null, bool? AllowAnonymous = null);

public sealed record RotateClientRequest(int? GraceSeconds);

public sealed record ClientSecretBody(string ClientId, string ClientSecret, DateTimeOffset? PreviousSecretExpiresAt);

public abstract class ProjectTenantEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    protected const string Root = "/v1/{project}";

    protected TenantDirectory Tenants { get; } = tenants;

    protected virtual bool PersonOnly => false;

    protected sealed override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        if (this.PersonOnly && caller.ActingAgentId is not null)
        {
            return AgentForbidden();
        }

        var person = new ProjectCaller.SignedIn(caller);
        await this.BeforeWriteAsync(person, cancellationToken);
        return await this.HandleAsync(person, cancellationToken);
    }

    protected virtual Task BeforeWriteAsync(ProjectCaller caller, CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken);

    protected override async Task<IResult> AuthenticateAndHandleAsync(CancellationToken cancellationToken)
    {
        var request = this.HttpContext.Request;
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || ApiKeys.Parse(header.Parameter) is not { } parsed)
        {
            return await base.AuthenticateAndHandleAsync(cancellationToken);
        }

        var secret = parsed.Presented;

        var keys = this.HttpContext.RequestServices.GetRequiredService<ApiKeys>();
        if (await keys.AuthenticateAsync(secret, cancellationToken) is not { } key)
        {
            this.HttpContext.Response.Headers.WWWAuthenticate = "Bearer realm=\"ts-auth\"";
            return TypedResults.Json(new ErrorBody(new ErrorDetail("unauthorized", "Present a live project secret API key or a terrascale sign-in access token.")),
                PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
        }

        await keys.RecordUseAsync(key, cancellationToken);
        if (HttpMethods.IsGet(request.Method))
        {
            return await this.HandleAsync(new ProjectCaller.SecretKey(key), cancellationToken);
        }

        var operation = this.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? throw new InvalidOperationException("A management operation must have a name.");
        try
        {
            await this.BeforeWriteAsync(new ProjectCaller.SecretKey(key), cancellationToken);
            return await this.Tenants.AuditKeyWriteAsync(key, operation, request.Path,
                token => this.HandleAsync(new ProjectCaller.SecretKey(key), token), cancellationToken);
        }
        catch (Exception error)
        {
            var status = error is ApiException failure ? failure.Status
                : AuthData.IsUnavailable(error) ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
            await this.Tenants.AuditKeyFailureAsync(key, operation, request.Path, status, CancellationToken.None);
            throw;
        }
    }

    protected async Task<string> AccessAsync(ProjectCaller caller, string minimum, CancellationToken cancellationToken, string? requiredScope = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var projectId = this.RouteId("project");
        switch (caller)
        {
            case ProjectCaller.SignedIn(var person):
                await this.Tenants.CheckAccessAsync(person.Id, projectId, minimum, cancellationToken);
                break;
            case ProjectCaller.SecretKey(var key):
                await this.Tenants.CheckKeyAccessAsync(key, projectId, requiredScope ?? (HttpMethods.IsGet(this.HttpContext.Request.Method) && minimum != Roles.Admin
                    ? TerraBaseScopes.AuthRead : TerraBaseScopes.AuthWrite), this.HttpContext.Connection.RemoteIpAddress, cancellationToken);

                break;
            default:
                throw new UnreachableException();
        }

        return projectId;
    }

    protected async Task<SettingsBody> SettingsAsync(string projectId, ProjectAuthSettingsRecord settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var services = this.HttpContext.RequestServices;
        var options = services.GetRequiredService<AuthOptions>();
        var data = services.GetRequiredService<AuthData>();
        var counts = await data.ReadAsync(connection => connection.QuerySingleAsync<CountsBody>(
            """
            SELECT (SELECT COUNT(*) FROM users WHERE instance_id = @projectId) AS users,
                (SELECT COUNT(*) FROM oidc_applications WHERE instance_id = @projectId) AS clients
            """, new { projectId }), cancellationToken);
        var issuer = OidcIssuer.Issuer(options, projectId).AbsoluteUri;
        return new(projectId, issuer, issuer + "/.well-known/openid-configuration", new Uri(options.PublicOrigin, $"ui/{projectId}/sign-in").AbsoluteUri,
            new(settings.Passcode, settings.Password, settings.SignUpMode, settings.MagicLink, settings.Username), new(settings.Name, settings.LogoUrl, settings.ThemeColor, settings.TitleSize, settings.DefaultTheme, settings.FooterLinks, settings.BackgroundColor, settings.TextColor, settings.EmailMessage),
            counts,
            [.. SocialProviders.Names.Select(name => settings.Provider(name) is { } record
                ? new ProviderBody(name, true, record.ClientId, record.Issuer, record.TeamId, record.KeyId, SocialProviders.Callback(options, projectId, name).AbsoluteUri, record.Tenant, record.OAuth2)
                : new ProviderBody(name, false, null, null, null, null, SocialProviders.Callback(options, projectId, name).AbsoluteUri))], settings.Configuration ?? ConfigurationBody.Default,
            new(settings.AttackProtection.BotChallenge, settings.AttackProtection.BreachedPasswords, settings.AttackProtection.AccountLockout, settings.AttackProtection.NewDeviceEmail,
                services.GetRequiredService<TurnstileKeys>().Configured), settings.Restrictions ?? RestrictionsBody.Default, settings.OrganizationPicker);
    }

    protected static ProjectUserBody Map(UserRecord user) => ProjectUserBody.From(user);

    protected static ClientBody Map(OidcApplication client, string? secret = null) => new(client.ClientId!, client.DisplayName ?? string.Empty, MachineClients.Policy(client) is null ? client.ClientType! : "machine",
        [.. client.RedirectUris], Ulid.Parse(client.ClientId).Time, secret, MachineClients.Policy(client)?.AllowedScopes,
        MachineClients.Policy(client)?.AllowedAudiences, MachineClients.Policy(client)?.TokenLifetimeMinutes, [.. client.AllowedOrigins], GuestSignIn.Allows(client));

    protected static Uri? ClientRedirect(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) && !value.Contains('#', StringComparison.Ordinal)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProjectStepUpRequest))]
[JsonSerializable(typeof(ProjectStepUpBody))]
[JsonSerializable(typeof(RestrictionsBody))]
[JsonSerializable(typeof(ProjectInvitationBody))]
[JsonSerializable(typeof(CreateProjectInvitationRequest))]
[JsonSerializable(typeof(ProjectInvitationPageBody))]
[JsonSerializable(typeof(WaitlistPageBody))]
[JsonSerializable(typeof(SettingsBody))]
[JsonSerializable(typeof(UpdateSettingsRequest))]
[JsonSerializable(typeof(SetProviderRequest))]
[JsonSerializable(typeof(ProjectUserBody))]
[JsonSerializable(typeof(ProjectUserPageBody))]
[JsonSerializable(typeof(ProjectSessionPageBody))]
[JsonSerializable(typeof(ClientBody))]
[JsonSerializable(typeof(ClientPageBody))]
[JsonSerializable(typeof(CreateClientRequest))]
[JsonSerializable(typeof(UpdateClientRequest))]
[JsonSerializable(typeof(RotateClientRequest))]
[JsonSerializable(typeof(ClientSecretBody))]
internal sealed partial class ProjectTenantJson : JsonSerializerContext;
