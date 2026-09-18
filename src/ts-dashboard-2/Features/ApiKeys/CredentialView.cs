namespace TerraScale.Dashboard2.Features.ApiKeys;

/// <summary>One row of the unified credentials table (API keys + M2M clients).</summary>
public sealed record CredentialView(
    string Kind,              // key | m2m
    string Id,
    string Name,
    string Prefix,
    string LastFour,
    string[] Scopes,
    string DatabaseId,
    string CreatedByEmail,
    string? LastUsedAt,
    string? ExpiresAt,
    string Status);
