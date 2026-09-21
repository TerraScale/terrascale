namespace TerraScale.Dashboard;

public sealed record SettingsCredentialView(
    string Id,
    string Type,            // API key | M2M application
    string Name,
    string Prefix,
    string LastFour,
    IReadOnlyList<string> Scopes,
    string Status,          // Active | Revoked
    string CreatedByEmail,
    string CreatedAt,
    string ExpiresAt,
    string LastUsedAt);
