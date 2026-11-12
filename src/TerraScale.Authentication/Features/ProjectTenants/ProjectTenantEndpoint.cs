using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>Sign-in methods; at least one of passcode and password stays on.</summary>
public sealed record MethodsBody(bool Passcode, bool Password, bool SignUp);

/// <summary>Hosted page branding; null shows the project's name and the default theme.</summary>
public sealed record BrandingBody(string? Name, string? LogoUrl, string? ThemeColor, int? TitleSize = null, string? DefaultTheme = null,
    IReadOnlyList<FooterLinkBody>? FooterLinks = null, string? BackgroundColor = null, string? TextColor = null, [property: JsonPropertyName("email_message")] string? EmailMessage = null);

/// <summary>A labelled HTTPS link underneath the hosted card.</summary>
public sealed record FooterLinkBody(string Label, string Url);

/// <summary>Project policy in addition to each OAuth client's exact redirect registration.</summary>
public sealed record ConfigurationBody(IReadOnlyList<string> RedirectUrls, int SessionLifetimeSeconds = 1209600,
    bool RequireMfa = false, bool AllowTotp = true, bool AllowPasskey = true, int? InactivityTimeoutMinutes = null, bool SingleSession = false)
{
    /// <summary>No additional redirect restriction and a fourteen-day absolute session.</summary>
    public static readonly ConfigurationBody Default = new([]);
}

/// <summary>How many users and clients the tenant has.</summary>
public sealed record CountsBody(long Users, long Clients);

/// <summary>A social sign-in provider; its secret is never answered. <c>callback_url</c> is registered at the provider.</summary>
public sealed record ProviderBody(string Provider, bool Configured, string? ClientId, string? Issuer, string? TeamId, string? KeyId, string CallbackUrl);

/// <summary>A project tenant's settings.</summary>
public sealed record SettingsBody(string ProjectId, string Issuer, string DiscoveryUrl, string SignInUrl, MethodsBody Methods, BrandingBody Branding, CountsBody Counts,
    IReadOnlyList<ProviderBody> Providers, ConfigurationBody? Configuration = null);

/// <summary><c>PUT /v1/{project}/providers/{provider}</c>: an omitted client_secret keeps the stored one.</summary>
public sealed record SetProviderRequest(string? ClientId, string? ClientSecret, string? Issuer = null, string? TeamId = null, string? KeyId = null);

/// <summary>Changes to the sign-in methods; an omitted method is unchanged.</summary>
public sealed record MethodsPatch(bool? Passcode, bool? Password, bool? SignUp);

/// <summary><c>PUT /v1/{project}/settings</c>: an omitted part is unchanged; a sent branding replaces the branding.</summary>
public sealed record UpdateSettingsRequest(MethodsPatch? Methods, BrandingBody? Branding, ConfigurationBody? Configuration = null);

/// <summary>An end user of the tenant.</summary>
public sealed record ProjectUserBody(string Id, string Email, DateTimeOffset CreatedAt, bool Disabled);

/// <summary>A page of end users.</summary>
public sealed record ProjectUserPageBody(IReadOnlyList<ProjectUserBody> Items, string? Next);

/// <summary>A live session of an end user.</summary>
public sealed record ProjectSessionBody(string Id, string UserId, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>A page of live sessions.</summary>
public sealed record ProjectSessionPageBody(IReadOnlyList<ProjectSessionBody> Items, string? Next);

/// <summary>An OAuth client of the tenant; <c>client_secret</c> only in the answer that created a confidential client.</summary>
public sealed record ClientBody(string ClientId, string Name, string Type, IReadOnlyList<string> RedirectUris, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null);

/// <summary>The tenant's clients, in one page.</summary>
public sealed record ClientPageBody(IReadOnlyList<ClientBody> Items, string? Next);

/// <summary><c>POST /v1/{project}/clients</c>.</summary>
public sealed record CreateClientRequest(string? Name, string? Type, IReadOnlyList<string>? RedirectUris);

public sealed record UpdateClientRequest(string? Name, IReadOnlyList<string>? RedirectUris);

public sealed record RotateClientRequest(int? GraceSeconds);

public sealed record ClientSecretBody(string ClientId, string ClientSecret, DateTimeOffset? PreviousSecretExpiresAt);

public abstract class ProjectTenantEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    protected const string Root = "/v1/{project}";

    protected TenantDirectory Tenants { get; } = tenants;

    protected sealed override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        this.HandleAsync(new ProjectCaller.SignedIn(caller), cancellationToken);

    protected abstract Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken);

    protected override async Task<IResult> AuthenticateAndHandleAsync(CancellationToken cancellationToken)
    {
        var request = this.HttpContext.Request;
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: 79 } secret || secret[26] != '_')
        {
            return await base.AuthenticateAndHandleAsync(cancellationToken);
        }

        var keys = this.HttpContext.RequestServices.GetRequiredService<ApiKeys>();
        if (await keys.AuthenticateAsync(secret, cancellationToken) is not { } key)
        {
            this.HttpContext.Response.Headers.WWWAuthenticate = "Bearer realm=\"ts-auth\"";
            return Results.Json(new ErrorBody(new ErrorDetail("unauthorized", "Present a live project secret API key or a terrascale sign-in access token.")),
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

    protected async Task<string> AccessAsync(ProjectCaller caller, string minimum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var projectId = this.RouteId("project");
        switch (caller)
        {
            case ProjectCaller.SignedIn(var person):
                await this.Tenants.CheckAccessAsync(person.Id, projectId, minimum, cancellationToken);
                break;
            case ProjectCaller.SecretKey(var key):
                await this.Tenants.CheckKeyAccessAsync(key, projectId, HttpMethods.IsGet(this.HttpContext.Request.Method) && minimum != Roles.Admin
                    ? TerraBaseScopes.AuthRead : TerraBaseScopes.AuthWrite, this.HttpContext.Connection.RemoteIpAddress, cancellationToken);

                break;
            default:
                throw new UnreachableException();
        }

        return projectId;
    }

    /// <summary>The settings as the API answers them.</summary>
    protected async Task<SettingsBody> SettingsAsync(string projectId, ProjectAuthSettingsRecord settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var services = this.HttpContext.RequestServices;
        var options = services.GetRequiredService<AuthOptions>();
        var data = services.GetRequiredService<AuthData>();
        var counts = await data.ReadAsync(async (db, token) => new CountsBody(
            await db.Users.LongCountAsync(user => user.InstanceId == projectId, token).ConfigureAwait(false),
            await db.OidcApplications.LongCountAsync(application => application.InstanceId == projectId, token).ConfigureAwait(false)), cancellationToken);
        var issuer = OidcIssuer.Issuer(options, projectId).AbsoluteUri;
        return new(projectId, issuer, issuer + "/.well-known/openid-configuration", new Uri(options.PublicOrigin, $"ui/{projectId}/sign-in").AbsoluteUri,
            new(settings.Passcode, settings.Password, settings.SignUp), new(settings.Name, settings.LogoUrl, settings.ThemeColor, settings.TitleSize, settings.DefaultTheme, settings.FooterLinks, settings.BackgroundColor, settings.TextColor, settings.EmailMessage),
            counts,
            [.. SocialProviders.Names.Select(name => settings.Provider(name) is { } record
                ? new ProviderBody(name, true, record.ClientId, record.Issuer, record.TeamId, record.KeyId, SocialProviders.Callback(options, projectId, name).AbsoluteUri)
                : new ProviderBody(name, false, null, null, null, null, SocialProviders.Callback(options, projectId, name).AbsoluteUri))], settings.Configuration ?? ConfigurationBody.Default);
    }

    /// <summary>An end user.</summary>
    protected static ProjectUserBody Map(UserRecord user) => new(user.Id, user.Email, user.CreatedAt, user.Disabled);

    /// <summary>A client, with its secret when just created.</summary>
    protected static ClientBody Map(OidcApplication client, string? secret = null) => new(client.ClientId!, client.DisplayName ?? string.Empty, client.ClientType!,
        [.. client.RedirectUris], NUlid.Ulid.Parse(client.ClientId).Time, secret);

    protected static Uri? ClientRedirect(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) && !value.Contains('#', StringComparison.Ordinal)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProjectStepUpRequest))]
[JsonSerializable(typeof(ProjectStepUpBody))]
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
