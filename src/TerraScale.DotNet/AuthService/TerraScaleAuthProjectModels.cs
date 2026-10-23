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
    [property: JsonPropertyName("theme_color")] string? ThemeColor);

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
    [property: JsonPropertyName("counts")] TerraScaleAuthProjectCounts Counts);

/// <summary>PUT /settings body. A null part is omitted from the request and stays unchanged.</summary>
public sealed record TerraScaleAuthProjectSettingsUpdate(
    [property: JsonPropertyName("methods"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectMethods? Methods = null,
    [property: JsonPropertyName("branding"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerraScaleAuthProjectBranding? Branding = null);

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
