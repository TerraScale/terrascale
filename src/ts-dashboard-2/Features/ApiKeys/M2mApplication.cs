namespace TerraScale.Dashboard2;

public sealed record M2mApplication(
    string ClientId,
    string ProjectId,
    string Name,
    string Status,
    string CreatedAt,
    string? LastUsedAt = null);
