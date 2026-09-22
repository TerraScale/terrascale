namespace TerraScale.Dashboard.Features.Databases.Models;

public sealed record RegionInfo(
    string Code,
    string Name,
    string Status,          // Live | Degraded | Offline
    int LatencyMs,
    int DatabaseCount);
