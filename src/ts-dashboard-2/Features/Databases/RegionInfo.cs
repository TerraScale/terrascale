namespace TerraScale.Dashboard2;

public sealed record RegionInfo(
    string Code,
    string Name,
    string Status,          // Live | Degraded | Offline
    int LatencyMs,
    int DatabaseCount);
