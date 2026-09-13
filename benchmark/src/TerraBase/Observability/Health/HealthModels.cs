namespace TerraBase;

/// <summary>A closed component vocabulary used by health checks.</summary>
public enum Component
{
    /// <summary>Storage and the write-ahead log.</summary>
    Storage,

    /// <summary>Replication links.</summary>
    Replication,

    /// <summary>Membership and admission.</summary>
    Membership,

    /// <summary>The private control plane.</summary>
    ControlPlane,

    /// <summary>Backup and restore.</summary>
    Backup,

    /// <summary>Metrics collection itself.</summary>
    Metrics,
}

/// <summary>The aggregate state of one health check.</summary>
public enum HealthStatus
{
    /// <summary>The component is healthy.</summary>
    Healthy,

    /// <summary>The component is serving with reduced margin.</summary>
    Degraded,

    /// <summary>The component cannot support its contract.</summary>
    Unhealthy,

    /// <summary>No trustworthy observation has been recorded.</summary>
    Unknown,
}

/// <summary>The process lifecycle used by readiness probes.</summary>
public enum ServiceState
{
    /// <summary>The process has started but is not serving.</summary>
    Starting,

    /// <summary>The process may accept traffic.</summary>
    Serving,

    /// <summary>The process drains existing traffic.</summary>
    Draining,

    /// <summary>The process has stopped.</summary>
    Stopped,
}

/// <summary>An immutable component health observation.</summary>
public sealed record HealthCheck(Component Component, HealthStatus Status);

/// <summary>An immutable health report for all bounded components.</summary>
public sealed record HealthReport(HealthStatus Status, IReadOnlyList<HealthCheck> Checks)
{
    /// <summary>Whether required components are observed healthy.</summary>
    public bool IsReady =>
        Checks.Where(check => check.Component is Component.Storage or Component.Replication or Component.Membership)
            .GroupBy(check => check.Component)
            .All(group => group.Count() == 1 && group.Single().Status == HealthStatus.Healthy);

    /// <summary>The HTTP status code for a readiness probe.</summary>
    public int ReadinessStatusCode => IsReady ? 200 : 503;

    /// <summary>The HTTP status code for a health probe.</summary>
    public int HealthStatusCode => Status is HealthStatus.Unhealthy or HealthStatus.Unknown ? 503 : 200;
}
