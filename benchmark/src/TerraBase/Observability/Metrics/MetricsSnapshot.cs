namespace TerraBase;

/// <summary>An immutable, internally consistent metrics snapshot.</summary>
public sealed partial class MetricsSnapshot
{
    internal MetricsSnapshot(
        ServiceState serviceState,
        bool started,
        bool ready,
        IReadOnlyList<RequestSeries> requests,
        HealthReport health)
    {
        ServiceState = serviceState;
        Started = started;
        Ready = ready;
        Requests = requests;
        Health = health;
    }

    /// <summary>The lifecycle state captured by this snapshot.</summary>
    public ServiceState ServiceState { get; }

    /// <summary>Whether the process reached the serving state.</summary>
    public bool Started { get; }

    /// <summary>Whether the process is ready to accept traffic.</summary>
    public bool Ready { get; }

    /// <summary>Request series in deterministic closed-label order.</summary>
    public IReadOnlyList<RequestSeries> Requests { get; }

    /// <summary>The component health report captured with the metrics.</summary>
    public HealthReport Health { get; }

    /// <summary>Whether the process is still alive.</summary>
    public bool IsLive => ServiceState != ServiceState.Stopped;
}
