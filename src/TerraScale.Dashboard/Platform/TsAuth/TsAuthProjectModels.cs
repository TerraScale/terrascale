using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed record TsAuthProjectMethods(
        [property: JsonPropertyName("passcode")] bool Passcode,
        [property: JsonPropertyName("password")] bool Password,
        [property: JsonPropertyName("sign_up")] bool SignUp);

    public sealed record TsAuthProjectBranding(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("logo_url")] string? LogoUrl,
        [property: JsonPropertyName("theme_color")] string? ThemeColor,
        [property: JsonPropertyName("title_size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TitleSize = null,
        [property: JsonPropertyName("default_theme"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultTheme = null,
        [property: JsonPropertyName("footer_links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TsAuthProjectFooterLink>? FooterLinks = null,
        [property: JsonPropertyName("background_color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BackgroundColor = null,
        [property: JsonPropertyName("text_color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextColor = null)
    {
        [JsonPropertyName("email_message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EmailMessage { get; init; }
    }

    public sealed record TsAuthProjectFooterLink(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("url")] string Url);

    public sealed record TsAuthProjectConfiguration(
        [property: JsonPropertyName("redirect_urls")] IReadOnlyList<string> RedirectUrls,
        [property: JsonPropertyName("session_lifetime_seconds")] int SessionLifetimeSeconds = 1209600);

    public sealed record TsAuthProjectCounts(
        [property: JsonPropertyName("users")] int Users,
        [property: JsonPropertyName("clients")] int Clients);

    public sealed record TsAuthProjectSettings(
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("discovery_url")] string DiscoveryUrl,
        [property: JsonPropertyName("sign_in_url")] string SignInUrl,
        [property: JsonPropertyName("methods")] TsAuthProjectMethods Methods,
        [property: JsonPropertyName("branding")] TsAuthProjectBranding Branding,
        [property: JsonPropertyName("counts")] TsAuthProjectCounts Counts,
        [property: JsonPropertyName("providers")] IReadOnlyList<TsAuthProjectProvider> Providers,
        [property: JsonPropertyName("configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsAuthProjectConfiguration? Configuration = null);

    public sealed record TsAuthProjectProvider(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("configured")] bool Configured,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("key_id")] string? KeyId,
        [property: JsonPropertyName("callback_url")] string CallbackUrl);

    public sealed record TsAuthProjectProviderUpdate(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null,
        [property: JsonPropertyName("issuer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Issuer = null,
        [property: JsonPropertyName("team_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TeamId = null,
        [property: JsonPropertyName("key_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? KeyId = null)
    {
        public override string ToString()
        {
            return "TsAuthProjectProviderUpdate { sensitive fields redacted }";
        }
    }

    public sealed record TsAuthProjectSettingsUpdate(
        [property: JsonPropertyName("methods"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsAuthProjectMethods? Methods = null,
        [property: JsonPropertyName("branding"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsAuthProjectBranding? Branding = null,
        [property: JsonPropertyName("configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsAuthProjectConfiguration? Configuration = null);

    public sealed record TsAuthProjectUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("disabled")] bool Disabled);

    public sealed record TsAuthUserExportPage(
        [property: JsonPropertyName("items")] IReadOnlyList<TsAuthDirectoryUser> Items,
        [property: JsonPropertyName("next")] string? Next,
        [property: JsonPropertyName("created_before")] DateTimeOffset CreatedBefore);

    public sealed record TsAuthDirectoryUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("email_verified")] bool EmailVerified,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("disabled")] bool Disabled,
        [property: JsonPropertyName("linked_providers")] IReadOnlyList<TsAuthLinkedProvider> LinkedProviders);

    public sealed record TsAuthLinkedProvider(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("subject")] string Subject);

    public sealed record TsAuthProjectSession(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    public sealed record TsAuthProjectOAuthClient(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret = null)
    {
        public override string ToString()
        {
            return "TsAuthProjectOAuthClient { sensitive fields redacted }";
        }
    }

    public sealed record TsAuthProjectCreateOAuthClient(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris);
}
