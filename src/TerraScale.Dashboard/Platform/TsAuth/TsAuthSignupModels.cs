using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthRestrictions(
    [property: JsonPropertyName("allowlist")] IReadOnlyList<string> Allowlist,
    [property: JsonPropertyName("blocklist")] IReadOnlyList<string> Blocklist,
    [property: JsonPropertyName("block_disposable_emails")] bool BlockDisposableEmails = false,
    [property: JsonPropertyName("block_email_aliases")] bool BlockEmailAliases = false);

public sealed record TsAuthProjectInvitation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("redirect_url")] string? RedirectUrl,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] string Status);

public sealed record TsAuthCreateProjectInvitation(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("redirect_url")] string? RedirectUrl = null);

public sealed record TsAuthWaitlistEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("status")] string Status);
