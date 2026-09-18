namespace TerraScale.Dashboard2;

public sealed record ProjectApiKey(
    string KeyId,
    string ProjectId,
    string Name,
    string Prefix,
    string LastFour,
    IReadOnlyList<string> Scopes,
    string? DatabaseId,
    string CreatedByEmail,
    string CreatedAt,
    string? ExpiresAt,
    string? LastUsedAt,
    string? RevokedAt)
{
    public bool IsActive => RevokedAt is null;
    public string Status => RevokedAt is not null ? "Revoked" : ExpiresAt is { } e && DateTimeOffset.TryParse(e, out var exp) && exp < DateTimeOffset.Now ? "Expired" : "Active";
    public string Masked => $"{Prefix}••••••••{LastFour}";
}
