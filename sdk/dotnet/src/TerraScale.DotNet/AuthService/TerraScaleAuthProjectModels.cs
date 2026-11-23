using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthProjectMethods(
    [property: JsonPropertyName("passcode")] bool Passcode,
    [property: JsonPropertyName("password")] bool Password,
    [property: JsonPropertyName("sign_up_mode")] string SignUpMode,
    [property: JsonPropertyName("magic_link")] bool MagicLink = false,
    [property: JsonPropertyName("username")] bool Username = false);

public sealed record TerraScaleAuthProjectBranding(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("logo_url")] string? LogoUrl,
    [property: JsonPropertyName("theme_color")] string? ThemeColor,
    [property: JsonPropertyName("title_size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TitleSize = null,
    [property: JsonPropertyName("default_theme"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultTheme = null,
    [property: JsonPropertyName("footer_links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TerraScaleAuthProjectFooterLink>? FooterLinks = null,
    [property: JsonPropertyName("background_color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BackgroundColor = null,
    [property: JsonPropertyName("text_color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextColor = null)
{
    [JsonPropertyName("email_message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmailMessage { get; init; }
}

public sealed record TerraScaleAuthProjectFooterLink(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("url")] string Url);

public sealed record TerraScaleAuthProjectConfiguration(
    [property: JsonPropertyName("redirect_urls")] IReadOnlyList<string> RedirectUrls,
    [property: JsonPropertyName("session_lifetime_seconds")] int SessionLifetimeSeconds = 1209600,
    [property: JsonPropertyName("require_mfa")] bool RequireMfa = false,
    [property: JsonPropertyName("allow_totp")] bool AllowTotp = true,
    [property: JsonPropertyName("allow_passkey")] bool AllowPasskey = true,
    [property: JsonPropertyName("inactivity_timeout_minutes")] int? InactivityTimeoutMinutes = null,
    [property: JsonPropertyName("single_session")] bool SingleSession = false);

public sealed record TerraScaleAuthProjectCounts(
    [property: JsonPropertyName("users")] int Users,
    [property: JsonPropertyName("clients")] int Clients);

public sealed record TerraScaleAuthProjectSettings(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("discovery_url")] string DiscoveryUrl,
    [property: JsonPropertyName("sign_in_url")] string SignInUrl,
    [property: JsonPropertyName("methods")] TerraScaleAuthProjectMethods Methods,
    [property: JsonPropertyName("branding")] TerraScaleAuthProjectBranding Branding,
    [property: JsonPropertyName("counts")] TerraScaleAuthProjectCounts Counts,
    [property: JsonPropertyName("providers")] IReadOnlyList<TerraScaleAuthProjectProvider> Providers,
    [property: JsonPropertyName("configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectConfiguration? Configuration = null,
    [property: JsonPropertyName("restrictions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthRestrictions? Restrictions = null,
    [property: JsonPropertyName("organization_picker")] bool OrganizationPicker = false);

public sealed record TerraScaleAuthProjectProvider(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("team_id")] string? TeamId,
    [property: JsonPropertyName("key_id")] string? KeyId,
    [property: JsonPropertyName("callback_url")] string CallbackUrl,
    [property: JsonPropertyName("tenant")] string? Tenant = null,
    [property: JsonPropertyName("oauth2")] TerraScaleAuthProjectOAuth2? OAuth2 = null);

public sealed record TerraScaleAuthProjectProviderUpdate(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null,
    [property: JsonPropertyName("issuer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Issuer = null,
    [property: JsonPropertyName("team_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TeamId = null,
    [property: JsonPropertyName("key_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? KeyId = null,
    [property: JsonPropertyName("tenant"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tenant = null,
    [property: JsonPropertyName("oauth2"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectOAuth2? OAuth2 = null)
{
    public override string ToString() => "TerraScaleAuthProjectProviderUpdate { sensitive fields redacted }";
}

public sealed record TerraScaleAuthProjectSettingsUpdate(
    [property: JsonPropertyName("methods"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectMethods? Methods = null,
    [property: JsonPropertyName("branding"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectBranding? Branding = null,
    [property: JsonPropertyName("configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectConfiguration? Configuration = null,
    [property: JsonPropertyName("restrictions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthRestrictions? Restrictions = null,
    [property: JsonPropertyName("organization_picker"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OrganizationPicker = null);

public sealed record TerraScaleAuthProjectUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("disabled")] bool Disabled)
{
    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }
    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; init; }
    [JsonPropertyName("external_id")]
    public string? ExternalId { get; init; }
    [JsonPropertyName("email_verified")]
    public bool EmailVerified { get; init; }
    [JsonPropertyName("public_metadata")]
    public JsonElement PublicMetadata { get; init; }
    [JsonPropertyName("private_metadata")]
    public JsonElement PrivateMetadata { get; init; }
    [JsonPropertyName("unsafe_metadata")]
    public JsonElement UnsafeMetadata { get; init; }
}


public sealed record TerraScaleAuthUserExportPage(
    [property: JsonPropertyName("items")] IReadOnlyList<TerraScaleAuthDirectoryUser> Items,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("created_before")] DateTimeOffset CreatedBefore);

public sealed record TerraScaleAuthDirectoryUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("email_verified")] bool EmailVerified,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("disabled")] bool Disabled,
    [property: JsonPropertyName("linked_providers")] IReadOnlyList<TerraScaleAuthLinkedProvider> LinkedProviders);

public sealed record TerraScaleAuthLinkedProvider(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("subject")] string Subject);

public sealed record TerraScaleAuthProjectSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record TerraScaleAuthProjectOAuthClient(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null,
    [property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string>? AllowedScopes = null,
    [property: JsonPropertyName("allowed_audiences")] IReadOnlyList<string>? AllowedAudiences = null,
    [property: JsonPropertyName("token_lifetime_minutes")] int? TokenLifetimeMinutes = null)
{
    public override string ToString() => "TerraScaleAuthProjectOAuthClient { sensitive fields redacted }";
}

public sealed record TerraScaleAuthProjectCreateOAuthClient(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
    [property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string>? AllowedScopes = null,
    [property: JsonPropertyName("allowed_audiences")] IReadOnlyList<string>? AllowedAudiences = null,
    [property: JsonPropertyName("token_lifetime_minutes")] int? TokenLifetimeMinutes = null);

public sealed record TerraScaleAuthProjectUpdateOAuthClient(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
    [property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string>? AllowedScopes = null,
    [property: JsonPropertyName("allowed_audiences")] IReadOnlyList<string>? AllowedAudiences = null,
    [property: JsonPropertyName("token_lifetime_minutes")] int? TokenLifetimeMinutes = null);

public sealed record TerraScaleAuthProjectRotateOAuthClient(
    [property: JsonPropertyName("grace_seconds")] int? GraceSeconds = null);

public sealed record TerraScaleAuthProjectClientSecret(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("previous_secret_expires_at")] DateTimeOffset? PreviousSecretExpiresAt)
{
    public override string ToString() => "TerraScaleAuthProjectClientSecret { sensitive fields redacted }";
}

public sealed record TerraScaleAuthProjectOAuth2(
    [property: JsonPropertyName("authorize_url")] string AuthorizeUrl,
    [property: JsonPropertyName("token_url")] string TokenUrl,
    [property: JsonPropertyName("userinfo_url")] string UserinfoUrl,
    [property: JsonPropertyName("scopes")] string Scopes,
    [property: JsonPropertyName("id_claim")] string IdClaim = "id",
    [property: JsonPropertyName("email_claim")] string EmailClaim = "email",
    [property: JsonPropertyName("email_verified_claim")] string EmailVerifiedClaim = "email_verified");
