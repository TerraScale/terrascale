using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>Sign-in methods; at least one of passcode and password stays on.</summary>
public sealed record MethodsBody(bool Passcode, bool Password, bool SignUp);

/// <summary>Hosted page branding; null shows the project's name and the default theme.</summary>
public sealed record BrandingBody(string? Name, string? LogoUrl, string? ThemeColor);

/// <summary>How many users and clients the tenant has.</summary>
public sealed record CountsBody(long Users, long Clients);

/// <summary>A project tenant's settings.</summary>
public sealed record SettingsBody(string ProjectId, string Issuer, string DiscoveryUrl, string SignInUrl, MethodsBody Methods, BrandingBody Branding, CountsBody Counts);

/// <summary>Changes to the sign-in methods; an omitted method is unchanged.</summary>
public sealed record MethodsPatch(bool? Passcode, bool? Password, bool? SignUp);

/// <summary><c>PUT /v1/{project}/settings</c>: an omitted part is unchanged; a sent branding replaces the branding.</summary>
public sealed record UpdateSettingsRequest(MethodsPatch? Methods, BrandingBody? Branding);

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

/// <summary>
/// A project tenant management endpoint (<c>/v1/{project}/…</c>): the caller's terrascale sign-in token, then its
/// effective project role on every request.
/// </summary>
public abstract class ProjectTenantEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <summary>The tenant's management root.</summary>
    protected const string Root = "/v1/{project}";

    /// <summary>The tenant operations.</summary>
    protected TenantDirectory Tenants { get; } = tenants;

    /// <summary>The project id, once the caller holds at least <paramref name="minimum"/> on it.</summary>
    protected async Task<string> AccessAsync(UserRecord caller, string minimum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var projectId = this.RouteId("project");
        await this.Tenants.CheckAccessAsync(caller.Id, projectId, minimum, cancellationToken);
        return projectId;
    }

    /// <summary>The settings as the API answers them.</summary>
    protected async Task<SettingsBody> SettingsAsync(string projectId, ProjectAuthSettingsRecord settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var services = this.HttpContext.RequestServices;
        var options = services.GetRequiredService<AuthOptions>();
        var store = services.GetRequiredService<AuthStore>();
        var issuer = OidcIssuer.Issuer(options, projectId).AbsoluteUri;
        return new(projectId, issuer, issuer + "/.well-known/openid-configuration", new Uri(options.PublicOrigin, $"ui/{projectId}/sign-in").AbsoluteUri,
            new(settings.Passcode, settings.Password, settings.SignUp), new(settings.Name, settings.LogoUrl, settings.ThemeColor),
            new(await store.CountAsync(AuthStore.UserCollection, projectId, cancellationToken), await store.CountAsync(AuthStore.OidcApplicationCollection, projectId, cancellationToken)));
    }

    /// <summary>An end user.</summary>
    protected static ProjectUserBody Map(UserRecord user) => new(user.Id, user.Email, user.CreatedAt, user.Disabled);

    /// <summary>A client, with its secret when just created.</summary>
    protected static ClientBody Map(OidcApplication client, string? secret = null) => new(client.ClientId!, client.DisplayName ?? string.Empty, client.ClientType!,
        [.. client.RedirectUris], NUlid.Ulid.Parse(client.ClientId).Time, secret);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SettingsBody))]
[JsonSerializable(typeof(UpdateSettingsRequest))]
[JsonSerializable(typeof(ProjectUserBody))]
[JsonSerializable(typeof(ProjectUserPageBody))]
[JsonSerializable(typeof(ProjectSessionPageBody))]
[JsonSerializable(typeof(ClientBody))]
[JsonSerializable(typeof(ClientPageBody))]
[JsonSerializable(typeof(CreateClientRequest))]
internal sealed partial class ProjectTenantJson : JsonSerializerContext;
