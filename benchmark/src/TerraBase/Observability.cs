using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TerraBase;

/// <summary>The private Prometheus endpoint contract.</summary>
public static class PrometheusMetrics
{
    /// <summary>The endpoint path owned by a TerraBase host.</summary>
    public const string Path = "/internal/metrics";

    /// <summary>The Prometheus 0.0.4 response content type.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>The maximum exposition body accepted by the engine.</summary>
    public const int MaxBodyBytes = 512 * 1024;

    /// <summary>The fixed request latency bucket boundaries, in seconds.</summary>
    public static readonly double[] RequestLatencyBuckets =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    /// <summary>Renders a metrics snapshot as Prometheus text.</summary>
    /// <param name="snapshot">The immutable snapshot.</param>
    /// <returns>The exposition body.</returns>
    public static string Render(MetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.RenderPrometheus();
    }
}

/// <summary>A closed Region vocabulary used by metrics and routing adapters.</summary>
public enum Region
{
    /// <summary>São Paulo.</summary>
    Gru,

    /// <summary>San Francisco.</summary>
    Sfo,

    /// <summary>Frankfurt.</summary>
    Fra,

    /// <summary>No admitted Region is known.</summary>
    Unknown,
}

/// <summary>A closed operation vocabulary for aggregate request metrics.</summary>
public enum Operation
{
    /// <summary>A point or range read.</summary>
    Read,

    /// <summary>A document mutation.</summary>
    Write,

    /// <summary>A query execution.</summary>
    Query,

    /// <summary>A document deletion.</summary>
    Delete,

    /// <summary>A reactive subscription operation.</summary>
    Subscribe,

    /// <summary>A backup operation.</summary>
    Backup,

    /// <summary>A restore operation.</summary>
    Restore,

    /// <summary>A repair operation.</summary>
    Repair,

    /// <summary>An operation without a more specific family.</summary>
    Other,
}

/// <summary>A closed operation outcome vocabulary.</summary>
public enum Outcome
{
    /// <summary>The operation completed successfully.</summary>
    Success,

    /// <summary>The operation failed.</summary>
    Error,

    /// <summary>Admission or validation refused the operation.</summary>
    Rejected,

    /// <summary>The operation exceeded its deadline.</summary>
    Timeout,

    /// <summary>A version precondition failed.</summary>
    Conflict,

    /// <summary>The requested object was absent.</summary>
    NotFound,
}

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

/// <summary>An immutable fixed-bucket latency histogram.</summary>
public sealed record HistogramSnapshot(
    IReadOnlyList<long> CumulativeBuckets,
    long Count,
    double SumSeconds);

/// <summary>A bounded request counter and latency series.</summary>
public sealed record RequestSeries(
    Region Region,
    Operation Operation,
    Outcome Outcome,
    long Total,
    HistogramSnapshot Duration);

/// <summary>An immutable, internally consistent metrics snapshot.</summary>
public sealed class MetricsSnapshot
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

    /// <summary>Renders this snapshot in Prometheus text format.</summary>
    /// <returns>The exposition body.</returns>
    public string RenderPrometheus()
    {
        var output = new StringBuilder();
        Help(output, "terrabase_liveness", "Whether the TerraBase process is live.", "gauge");
        output.Append("terrabase_liveness ").Append(IsLive ? '1' : '0').Append('\n');
        Help(output, "terrabase_started", "Whether the TerraBase process reached serving.", "gauge");
        output.Append("terrabase_started ").Append(Started ? '1' : '0').Append('\n');
        Help(output, "terrabase_ready", "Whether required TerraBase components are healthy.", "gauge");
        output.Append("terrabase_ready ").Append(Ready ? '1' : '0').Append('\n');

        Help(output, "terrabase_requests_total", "Completed TerraBase requests.", "counter");
        Help(output, "terrabase_request_duration_seconds", "TerraBase request duration.", "histogram");
        foreach (var series in Requests)
        {
            var labels = $"region=\"{Label(series.Region)}\",operation=\"{Label(series.Operation)}\",outcome=\"{Label(series.Outcome)}\"";
            output.Append("terrabase_requests_total{").Append(labels).Append("} ").Append(series.Total).Append('\n');
            for (var index = 0; index < PrometheusMetrics.RequestLatencyBuckets.Length; index++)
            {
                output.Append("terrabase_request_duration_seconds_bucket{")
                    .Append(labels).Append(",le=\"")
                    .Append(PrometheusMetrics.RequestLatencyBuckets[index].ToString("G", CultureInfo.InvariantCulture))
                    .Append("\"} ").Append(series.Duration.CumulativeBuckets[index]).Append('\n');
            }

            output.Append("terrabase_request_duration_seconds_bucket{").Append(labels).Append(",le=\"+Inf\"} ")
                .Append(series.Duration.Count).Append('\n');
            output.Append("terrabase_request_duration_seconds_count{").Append(labels).Append("} ")
                .Append(series.Duration.Count).Append('\n');
            output.Append("terrabase_request_duration_seconds_sum{").Append(labels).Append("} ")
                .Append(series.Duration.SumSeconds.ToString("G", CultureInfo.InvariantCulture)).Append('\n');
        }

        Help(output, "terrabase_component_health", "Current bounded component health.", "gauge");
        foreach (var check in Health.Checks)
        {
            output.Append("terrabase_component_health{component=\"").Append(Label(check.Component)).Append("\"} ")
                .Append(HealthGauge(check.Status).ToString("G", CultureInfo.InvariantCulture)).Append('\n');
        }

        if (Encoding.UTF8.GetByteCount(output.ToString()) > PrometheusMetrics.MaxBodyBytes)
        {
            throw new ExpositionTooLargeException(PrometheusMetrics.MaxBodyBytes);
        }

        return output.ToString();
    }

    private static void Help(StringBuilder output, string name, string help, string type)
    {
        output.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        output.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static double HealthGauge(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 1,
        HealthStatus.Degraded => 0.5,
        HealthStatus.Unhealthy => 0,
        _ => -1,
    };

    private static string Label<T>(T value) where T : struct, Enum => value switch
    {
        Region region => region switch { Region.Gru => "GRU", Region.Sfo => "SFO", Region.Fra => "FRA", _ => "unknown" },
        Operation operation => operation switch
        {
            Operation.Read => "read", Operation.Write => "write", Operation.Query => "query", Operation.Delete => "delete",
            Operation.Subscribe => "subscribe", Operation.Backup => "backup", Operation.Restore => "restore",
            Operation.Repair => "repair", _ => "other",
        },
        Outcome outcome => outcome switch
        {
            Outcome.Success => "success", Outcome.Error => "error", Outcome.Rejected => "rejected",
            Outcome.Timeout => "timeout", Outcome.Conflict => "conflict", _ => "not_found",
        },
        Component component => component switch
        {
            Component.Storage => "storage", Component.Replication => "replication", Component.Membership => "membership",
            Component.ControlPlane => "control_plane", Component.Backup => "backup", _ => "metrics",
        },
        _ => value.ToString()!.ToLowerInvariant(),
    };
}

/// <summary>Raised when a Prometheus body exceeds its response bound.</summary>
public sealed class ExpositionTooLargeException : Exception
{
    /// <summary>Creates a size-bound exception.</summary>
    /// <param name="limit">The maximum accepted body size in bytes.</param>
    public ExpositionTooLargeException(int limit)
        : base($"The Prometheus exposition exceeds the {limit} byte limit.")
    {
        Limit = limit;
    }

    /// <summary>The maximum accepted body size.</summary>
    public int Limit { get; }
}

/// <summary>A read-only source for Prometheus adapters.</summary>
public interface IMetricsSource
{
    /// <summary>Takes one immutable metrics snapshot.</summary>
    MetricsSnapshot Snapshot();

    /// <summary>Renders one bounded exposition body.</summary>
    string MetricsBody();
}

/// <summary>A lock-protected, closed-label metrics registry.</summary>
public sealed class MetricsRegistry : IMetricsSource
{
    private sealed class MutableSeries
    {
        public long Total;
        public long[] Buckets { get; } = new long[PrometheusMetrics.RequestLatencyBuckets.Length];
        public double SumSeconds;
    }

    private readonly object gate = new();
    private readonly Dictionary<(Region Region, Operation Operation, Outcome Outcome), MutableSeries> requests = new();
    private readonly Dictionary<Operation, long> inFlight = new();
    private readonly HealthRegistry health = new();
    private ServiceState serviceState = ServiceState.Starting;
    private bool started;

    /// <summary>Records one bounded request and latency.</summary>
    /// <param name="region">The closed Region label.</param>
    /// <param name="operation">The closed operation label.</param>
    /// <param name="outcome">The closed outcome label.</param>
    /// <param name="duration">The observed request duration.</param>
    public void RecordRequest(Region region, Operation operation, Outcome outcome, TimeSpan duration)
    {
        var seconds = Math.Max(0, duration.TotalSeconds);
        lock (gate)
        {
            var series = requests.GetValueOrDefault((region, operation, outcome));
            if (series is null)
            {
                series = new MutableSeries();
                requests[(region, operation, outcome)] = series;
            }

            series.Total = checked(series.Total + 1);
            series.SumSeconds += seconds;
            for (var index = 0; index < PrometheusMetrics.RequestLatencyBuckets.Length; index++)
            {
                if (seconds <= PrometheusMetrics.RequestLatencyBuckets[index])
                {
                    series.Buckets[index] = checked(series.Buckets[index] + 1);
                    break;
                }
            }
        }
    }

    /// <summary>Begins a request scope that records completion on disposal.</summary>
    /// <param name="region">The closed Region label.</param>
    /// <param name="operation">The closed operation label.</param>
    /// <returns>The request scope.</returns>
    public RequestScope BeginRequest(Region region, Operation operation)
    {
        lock (gate)
        {
            inFlight[operation] = checked(inFlight.GetValueOrDefault(operation) + 1);
        }

        return new RequestScope(this, region, operation);
    }

    /// <summary>Records a component health observation.</summary>
    public void SetComponentHealth(Component component, HealthStatus status) => health.Set(component, status);

    /// <summary>Returns the current component report.</summary>
    public HealthReport Health() => health.Snapshot();

    /// <summary>Transitions the process lifecycle.</summary>
    /// <param name="next">The target state.</param>
    /// <exception cref="InvalidOperationException">The transition is not allowed.</exception>
    public void SetServiceState(ServiceState next)
    {
        lock (gate)
        {
            if (!CanTransition(serviceState, next))
            {
                throw new InvalidOperationException($"Service state cannot move from {serviceState} to {next}.");
            }

            serviceState = next;
            started |= next == ServiceState.Serving;
        }
    }

    /// <inheritdoc />
    public MetricsSnapshot Snapshot()
    {
        lock (gate)
        {
            var report = health.Snapshot();
            var requestSnapshots = requests
                .OrderBy(pair => pair.Key.Region)
                .ThenBy(pair => pair.Key.Operation)
                .ThenBy(pair => pair.Key.Outcome)
                .Select(pair => new RequestSeries(
                    pair.Key.Region,
                    pair.Key.Operation,
                    pair.Key.Outcome,
                    pair.Value.Total,
                    ToSnapshot(pair.Value)))
                .ToArray();
            return new MetricsSnapshot(serviceState, started, serviceState == ServiceState.Serving && report.IsReady, requestSnapshots, report);
        }
    }

    /// <inheritdoc />
    public string MetricsBody() => Snapshot().RenderPrometheus();

    internal void Complete(RequestScope scope, Outcome outcome)
    {
        lock (gate)
        {
            inFlight[scope.Operation] = Math.Max(0, inFlight.GetValueOrDefault(scope.Operation) - 1);
        }

        RecordRequest(scope.Region, scope.Operation, outcome, Stopwatch.GetElapsedTime(scope.Started));
    }

    private static HistogramSnapshot ToSnapshot(MutableSeries series)
    {
        var buckets = new long[series.Buckets.Length];
        var cumulative = 0L;
        for (var index = 0; index < buckets.Length; index++)
        {
            cumulative = checked(cumulative + series.Buckets[index]);
            buckets[index] = cumulative;
        }

        return new HistogramSnapshot(buckets, series.Total, series.SumSeconds);
    }

    private static bool CanTransition(ServiceState from, ServiceState to) =>
        from == to
        || (from, to) switch
        {
            (ServiceState.Starting, ServiceState.Serving or ServiceState.Draining or ServiceState.Stopped) => true,
            (ServiceState.Serving, ServiceState.Draining or ServiceState.Stopped) => true,
            (ServiceState.Draining, ServiceState.Stopped) => true,
            _ => false,
        };

    /// <summary>A disposable request timing scope.</summary>
    public sealed class RequestScope : IDisposable
    {
        private readonly MetricsRegistry registry;
        private int completed;

        internal RequestScope(MetricsRegistry registry, Region region, Operation operation)
        {
            this.registry = registry;
            Region = region;
            Operation = operation;
            Started = Stopwatch.GetTimestamp();
        }

        /// <summary>The request Region.</summary>
        public Region Region { get; }

        /// <summary>The request operation.</summary>
        public Operation Operation { get; }

        /// <summary>The monotonic start timestamp.</summary>
        internal long Started { get; }

        /// <summary>Completes this scope with an outcome.</summary>
        /// <param name="outcome">The closed operation outcome.</param>
        public void Complete(Outcome outcome)
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
            {
                registry.Complete(this, outcome);
            }
        }

        /// <inheritdoc />
        public void Dispose() => Complete(Outcome.Success);
    }
}
