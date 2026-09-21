namespace TerraScale.Dashboard;

public sealed record M2mApplication(
    string ClientId,
    string ProjectId,
    string Name,
    string Status,
    string CreatedAt,
    string? LastUsedAt = null);
