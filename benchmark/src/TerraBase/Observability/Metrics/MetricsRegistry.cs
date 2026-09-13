using System.Diagnostics;

namespace TerraBase;

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
            RecordRequestLocked(region, operation, outcome, seconds);
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
            var inFlightSnapshots = inFlight
                .OrderBy(pair => pair.Key)
                .Select(pair => new InFlightSeries(pair.Key, pair.Value))
                .ToArray();
            return new MetricsSnapshot(
                serviceState,
                started,
                serviceState == ServiceState.Serving && report.IsReady,
                requestSnapshots,
                inFlightSnapshots,
                report);
        }
    }

    /// <inheritdoc />
    public string MetricsBody() => Snapshot().RenderPrometheus();

    internal void Complete(RequestScope scope, Outcome outcome)
    {
        var seconds = Math.Max(0, Stopwatch.GetElapsedTime(scope.Started).TotalSeconds);
        lock (gate)
        {
            inFlight[scope.Operation] = Math.Max(0, inFlight.GetValueOrDefault(scope.Operation) - 1);
            RecordRequestLocked(scope.Region, scope.Operation, outcome, seconds);
        }
    }

    private void RecordRequestLocked(Region region, Operation operation, Outcome outcome, double seconds)
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
