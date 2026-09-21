using System.Collections.Immutable;

namespace TerraScale.Dashboard.Features.Organization.Models;

public sealed record DashboardProject(
    string Id,
    string OrganizationId,
    string Name,
    string Region,
    string CreatedAt,
    bool IsDefault = false,
    string Status = "Active",
    string? Slug = null,
    string BackupRegion = "",
    ImmutableArray<string> Regions = default);
