using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Project signup restrictions; lists contain normalized emails or exact domains.</summary>
public sealed record TerraScaleAuthRestrictions(
    [property: JsonPropertyName("allowlist")] IReadOnlyList<string> Allowlist,
    [property: JsonPropertyName("blocklist")] IReadOnlyList<string> Blocklist,
    [property: JsonPropertyName("block_disposable_emails")] bool BlockDisposableEmails = false,
    [property: JsonPropertyName("block_email_aliases")] bool BlockEmailAliases = false);

/// <summary>An invitation without its bearer token.</summary>
public sealed record TerraScaleAuthProjectInvitation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("redirect_url")] string? RedirectUrl,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] string Status);

/// <summary>Create an invitation, optionally continuing to a registered project redirect URL.</summary>
public sealed record TerraScaleAuthCreateProjectInvitation(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("redirect_url")] string? RedirectUrl = null);

/// <summary>A project waitlist entry.</summary>
public sealed record TerraScaleAuthWaitlistEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("status")] string Status);
