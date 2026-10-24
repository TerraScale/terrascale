using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>A project tenant's sign-in methods. At least one of Passcode and Password stays on; SignUp false hides self sign-up.</summary>
public sealed record TerraScaleAuthProjectMethods(
    [property: JsonPropertyName("passcode")] bool Passcode,
    [property: JsonPropertyName("password")] bool Password,
    [property: JsonPropertyName("sign_up")] bool SignUp);

/// <summary>A project tenant's hosted-page branding. Null clears a value: LogoUrl is an https URL, ThemeColor is
/// <c>#rrggbb</c> and Name is 1 to 64 characters.</summary>
public sealed record TerraScaleAuthProjectBranding(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("logo_url")] string? LogoUrl,
    [property: JsonPropertyName("theme_color")] string? ThemeColor,
    [property: JsonPropertyName("title_size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TitleSize = null,
    [property: JsonPropertyName("default_theme"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultTheme = null,
    [property: JsonPropertyName("footer_links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TerraScaleAuthProjectFooterLink>? FooterLinks = null,
    [property: JsonPropertyName("background_color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BackgroundColor = null,
    [property: JsonPropertyName("text_color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextColor = null);

/// <summary>A labelled HTTPS footer link underneath the hosted authentication card.</summary>
public sealed record TerraScaleAuthProjectFooterLink(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("url")] string Url);

/// <summary>Project redirect allowlist and absolute session duration, in seconds. An empty list adds no
/// restriction to the OAuth client's exact registered redirects; the default duration is fourteen days.</summary>
public sealed record TerraScaleAuthProjectConfiguration(
    [property: JsonPropertyName("redirect_urls")] IReadOnlyList<string> RedirectUrls,
    [property: JsonPropertyName("session_lifetime_seconds")] int SessionLifetimeSeconds = 1209600);

/// <summary>Counts of a project tenant's end users and OAuth clients.</summary>
public sealed record TerraScaleAuthProjectCounts(
    [property: JsonPropertyName("users")] int Users,
    [property: JsonPropertyName("clients")] int Clients);

/// <summary>A project tenant's settings, issuer and hosted sign-in URLs.</summary>
public sealed record TerraScaleAuthProjectSettings(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("discovery_url")] string DiscoveryUrl,
    [property: JsonPropertyName("sign_in_url")] string SignInUrl,
    [property: JsonPropertyName("methods")] TerraScaleAuthProjectMethods Methods,
    [property: JsonPropertyName("branding")] TerraScaleAuthProjectBranding Branding,
    [property: JsonPropertyName("counts")] TerraScaleAuthProjectCounts Counts,
    [property: JsonPropertyName("providers")] IReadOnlyList<TerraScaleAuthProjectProvider> Providers,
    [property: JsonPropertyName("configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectConfiguration? Configuration = null);

/// <summary>A social sign-in provider of a project tenant: <c>google</c>, <c>github</c>, <c>apple</c> or <c>oidc</c>.
/// Unconfigured providers have null ClientId, Issuer, TeamId and KeyId. CallbackUrl is the redirect URL to register at
/// the provider. The client secret is never returned.</summary>
public sealed record TerraScaleAuthProjectProvider(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("team_id")] string? TeamId,
    [property: JsonPropertyName("key_id")] string? KeyId,
    [property: JsonPropertyName("callback_url")] string CallbackUrl);

/// <summary>PUT /providers/{provider} body. ClientSecret is required the first time; null keeps the stored secret.
/// Apple takes TeamId, KeyId and the .p8 private key as ClientSecret; oidc takes an https Issuer.</summary>
public sealed record TerraScaleAuthProjectProviderUpdate(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null,
    [property: JsonPropertyName("issuer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Issuer = null,
    [property: JsonPropertyName("team_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TeamId = null,
    [property: JsonPropertyName("key_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? KeyId = null)
{
    /// <inheritdoc />
    public override string ToString() => "TerraScaleAuthProjectProviderUpdate { sensitive fields redacted }";
}

/// <summary>PUT /settings body. A null part is omitted from the request and stays unchanged.</summary>
public sealed record TerraScaleAuthProjectSettingsUpdate(
    [property: JsonPropertyName("methods"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectMethods? Methods = null,
    [property: JsonPropertyName("branding"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectBranding? Branding = null,
    [property: JsonPropertyName("configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectConfiguration? Configuration = null);

/// <summary>A project tenant end user. A disabled user cannot sign in.</summary>
public sealed record TerraScaleAuthProjectUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("disabled")] bool Disabled);

/// <summary>A live project tenant end-user session.</summary>
public sealed record TerraScaleAuthProjectSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>A project tenant OAuth client. ClientSecret is present only in the creation response of a
/// confidential client; display it once and never log or persist the record.</summary>
public sealed record TerraScaleAuthProjectOAuthClient(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null)
{
    /// <inheritdoc />
    public override string ToString() => "TerraScaleAuthProjectOAuthClient { sensitive fields redacted }";
}

/// <summary>POST /clients body. Type is <c>public</c> or <c>confidential</c>; 1 to 10 absolute https redirect URIs
/// (http only on localhost and loopback), without fragments.</summary>
public sealed record TerraScaleAuthProjectCreateOAuthClient(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris);
