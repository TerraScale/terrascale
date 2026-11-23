using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed record McpClientRow
{
    public string? OriginalVersion { get; set; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string RedirectUris { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime LastActivityAt { get; set; }

    public DateTime? FirstSignInCompletedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public required string Version { get; set; }
}

public sealed record McpCodeRow
{
    public required string Id { get; init; }

    public required string CodeHash { get; init; }

    public required string ClientId { get; init; }

    public required string CredentialId { get; init; }

    public required string UserId { get; init; }

    public required string RedirectUri { get; init; }

    public required string CodeChallenge { get; init; }

    public required string Resource { get; set; }

    public required string ClientName { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public required DateTime TokenExpiresAt { get; init; }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class McpRowJson : JsonSerializerContext;
