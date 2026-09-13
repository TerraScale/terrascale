namespace TerraBase;

/// <summary>Process-local component health storage.</summary>
public sealed class HealthRegistry
{
    private static readonly Component[] Components = Enum.GetValues<Component>();
    private static readonly Component[] RequiredComponents =
        [Component.Storage, Component.Replication, Component.Membership];
    private readonly object gate = new();
    private readonly Dictionary<Component, HealthStatus> statuses = new();

    /// <summary>Records one closed component status.</summary>
    /// <param name="component">The component.</param>
    /// <param name="status">The observed status.</param>
    public void Set(Component component, HealthStatus status)
    {
        lock (gate)
        {
            statuses[component] = status;
        }
    }

    /// <summary>Takes a stable report containing every component slot.</summary>
    /// <returns>The health report.</returns>
    public HealthReport Snapshot()
    {
        lock (gate)
        {
            var checks = Components
                .Select(component => new HealthCheck(component, statuses.GetValueOrDefault(component, HealthStatus.Unknown)))
                .ToArray();
            var status = checks.Any(check => check.Status == HealthStatus.Unhealthy)
                ? HealthStatus.Unhealthy
                : checks.Any(check => check.Status == HealthStatus.Unknown)
                    ? HealthStatus.Unknown
                    : checks.Any(check => check.Status == HealthStatus.Degraded)
                        ? HealthStatus.Degraded
                        : HealthStatus.Healthy;
            return new HealthReport(status, checks);
        }
    }

    /// <summary>Returns whether every required component is healthy.</summary>
    public bool IsReady()
    {
        lock (gate)
        {
            return RequiredComponents.All(component => statuses.GetValueOrDefault(component) == HealthStatus.Healthy);
        }
    }
}
