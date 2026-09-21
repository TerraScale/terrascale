namespace TerraScale.Dashboard.Features.Engine.ViewModels;

/// <summary>One Prometheus metric family from the TerraBase /internal/metrics surface.</summary>
public sealed record EngineMetricInfo(string Name, string Help);
