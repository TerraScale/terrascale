namespace TerraScale.Dashboard2;

/// <summary>API Keys feature data: the machine-client credential list the
/// original loads from the auth service (full column parity with API keys),
/// plus the one-time secret channel for create/rotate reveals.</summary>
public sealed record M2mCredential(
    string ClientId,
    string ProjectId,
    string Name,
    string Prefix,
    string LastFour,
    string[] Scopes,
    string DatabaseId,        // "" = Project
    string CreatedByEmail,
    string CreatedAt,
    string? ExpiresAt,
    string? LastUsedAt,
    string? RevokedAt)
{
    public string Status =>
        RevokedAt is not null ? "Revoked"
        : ExpiresAt is { } e && DateTimeOffset.TryParse(e, out var exp) && (exp - DateTimeOffset.Now).TotalDays < 14 ? "Expiring"
        : "Active";
}
