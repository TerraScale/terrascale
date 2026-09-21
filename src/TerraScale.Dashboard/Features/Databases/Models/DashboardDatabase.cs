namespace TerraScale.Dashboard.Features.Databases.Models;

public sealed record DashboardDatabase(
    string Id,
    string ProjectId,
    string Name,
    string Status,          // Active | Provisioning | Degraded
    string PrimaryRegion,
    string? BackupRegion,
    long SizeMb,
    long DocumentCount,
    string CreatedAt,
    string Version = "1",
    string? BranchOf = null);
