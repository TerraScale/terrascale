using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record AccessTokenRow
{
    public required string Id { get; init; }

    public required string UserId { get; init; }

    public required string Name { get; init; }

    public required string LastFour { get; init; }

    public required string SecretHash { get; init; }

    public required string CredentialId { get; init; }

    public string? McpClientId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public DateTime? RevokedAt { get; set; }
}
