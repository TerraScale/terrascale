namespace TerraScale.Dashboard.Features.ApiKeys.Models;

public sealed record M2mApplication(
    string ClientId,
    string ProjectId,
    string Name,
    string Status,
    string CreatedAt,
    string? LastUsedAt = null);
