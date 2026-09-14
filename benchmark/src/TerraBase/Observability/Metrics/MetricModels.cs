namespace TerraBase;

/// <summary>One of the latency percentiles exposed by a metric summary.</summary>
public enum LatencyPercentile
{
    /// <summary>The median latency.</summary>
    P50,

    /// <summary>The latency below which ninety-five percent of observations fall.</summary>
    P95,

    /// <summary>The latency below which ninety-nine percent of observations fall.</summary>
    P99,
}

/// <summary>
/// A bounded latency summary. All values use <see cref="TimeSpan"/> so callers
/// cannot accidentally mix milliseconds and seconds.
/// </summary>
public sealed record LatencyPercentiles
{
    /// <summary>Creates a latency summary from its three percentile values.</summary>
    /// <param name="p50">The median latency.</param>
    /// <param name="p95">The p95 latency.</param>
    /// <param name="p99">The p99 latency.</param>
    /// <param name="sampleCount">The number of observations used to calculate the values.</param>
    public LatencyPercentiles(TimeSpan p50, TimeSpan p95, TimeSpan p99, long sampleCount)
    {
        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        EnsureNonNegative(p50, nameof(p50));
        EnsureNonNegative(p95, nameof(p95));
        EnsureNonNegative(p99, nameof(p99));
        if (p50 > p95)
        {
            throw new ArgumentException("p50 cannot exceed p95.", nameof(p50));
        }

        if (p95 > p99)
        {
            throw new ArgumentException("p95 cannot exceed p99.", nameof(p95));
        }

        P50 = p50;
        P95 = p95;
        P99 = p99;
        SampleCount = sampleCount;
    }

    /// <summary>Creates a latency summary using sample count as the first argument.</summary>
    /// <param name="sampleCount">The number of observations used to calculate the values.</param>
    /// <param name="p50">The median latency.</param>
    /// <param name="p95">The p95 latency.</param>
    /// <param name="p99">The p99 latency.</param>
    public LatencyPercentiles(long sampleCount, TimeSpan p50, TimeSpan p95, TimeSpan p99)
        : this(p50, p95, p99, sampleCount)
    {
    }

    /// <summary>An empty summary with no observations.</summary>
    public static LatencyPercentiles Empty { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);

    /// <summary>The p50 latency.</summary>
    public TimeSpan P50 { get; }

    /// <summary>The p95 latency.</summary>
    public TimeSpan P95 { get; }

    /// <summary>The p99 latency.</summary>
    public TimeSpan P99 { get; }

    /// <summary>The number of observations used to calculate this summary.</summary>
    public long SampleCount { get; }

    /// <summary>The p50 latency in milliseconds.</summary>
    public double P50Milliseconds => P50.TotalMilliseconds;

    /// <summary>The p95 latency in milliseconds.</summary>
    public double P95Milliseconds => P95.TotalMilliseconds;

    /// <summary>The p99 latency in milliseconds.</summary>
    public double P99Milliseconds => P99.TotalMilliseconds;

    /// <summary>Returns one percentile value by its closed percentile kind.</summary>
    /// <param name="percentile">The requested percentile.</param>
    /// <returns>The latency at the requested percentile.</returns>
    public TimeSpan Get(LatencyPercentile percentile) => percentile switch
    {
        LatencyPercentile.P50 => P50,
        LatencyPercentile.P95 => P95,
        LatencyPercentile.P99 => P99,
        _ => throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Unknown latency percentile."),
    };

    /// <summary>Creates a latency summary from millisecond values.</summary>
    /// <param name="sampleCount">The number of observations used to calculate the values.</param>
    /// <param name="p50Milliseconds">The p50 value in milliseconds.</param>
    /// <param name="p95Milliseconds">The p95 value in milliseconds.</param>
    /// <param name="p99Milliseconds">The p99 value in milliseconds.</param>
    /// <returns>The validated summary.</returns>
    public static LatencyPercentiles FromMilliseconds(
        long sampleCount,
        double p50Milliseconds,
        double p95Milliseconds,
        double p99Milliseconds) =>
        new(
            sampleCount,
            FromMilliseconds(p50Milliseconds, nameof(p50Milliseconds)),
            FromMilliseconds(p95Milliseconds, nameof(p95Milliseconds)),
            FromMilliseconds(p99Milliseconds, nameof(p99Milliseconds)));

    private static TimeSpan FromMilliseconds(double milliseconds, string name)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        try
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(
                name,
                milliseconds,
                $"The latency value exceeds the TimeSpan range: {exception.Message}");
        }
    }

    private static void EnsureNonNegative(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>Managed allocation observations for one measurement window.</summary>
public sealed record AllocationMetrics
{
    /// <summary>Creates an allocation measurement.</summary>
    /// <param name="allocatedBytes">The number of allocated bytes.</param>
    /// <param name="allocationCount">The number of allocation events.</param>
    /// <param name="operationCount">The number of operations represented by the window.</param>
    public AllocationMetrics(long allocatedBytes, long allocationCount, long operationCount = 0)
    {
        EnsureNonNegative(allocatedBytes, nameof(allocatedBytes));
        EnsureNonNegative(allocationCount, nameof(allocationCount));
        EnsureNonNegative(operationCount, nameof(operationCount));
        AllocatedBytes = allocatedBytes;
        AllocationCount = allocationCount;
        OperationCount = operationCount;
    }

    /// <summary>The number of allocated bytes.</summary>
    public long AllocatedBytes { get; }

    /// <summary>The number of allocation events.</summary>
    public long AllocationCount { get; }

    /// <summary>The number of operations represented by the sample.</summary>
    public long OperationCount { get; }

    /// <summary>The allocated bytes per operation, when operations were supplied.</summary>
    public double? AllocatedBytesPerOperation =>
        OperationCount == 0 ? null : (double)AllocatedBytes / OperationCount;

    private static void EnsureNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>CPU and resident-memory observations for one measurement window.</summary>
public sealed record CpuRamMetrics
{
    /// <summary>Creates a CPU and memory measurement.</summary>
    /// <param name="cpuPercent">The process CPU utilization percentage.</param>
    /// <param name="workingSetBytes">The resident working-set size in bytes.</param>
    /// <param name="managedHeapBytes">The managed heap size in bytes.</param>
    public CpuRamMetrics(double cpuPercent, long workingSetBytes, long managedHeapBytes)
    {
        if (!double.IsFinite(cpuPercent) || cpuPercent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuPercent));
        }

        EnsureNonNegative(workingSetBytes, nameof(workingSetBytes));
        EnsureNonNegative(managedHeapBytes, nameof(managedHeapBytes));
        CpuPercent = cpuPercent;
        WorkingSetBytes = workingSetBytes;
        ManagedHeapBytes = managedHeapBytes;
    }

    /// <summary>The process CPU utilization percentage.</summary>
    public double CpuPercent { get; }

    /// <summary>The resident working-set size in bytes.</summary>
    public long WorkingSetBytes { get; }

    /// <summary>The managed heap size in bytes.</summary>
    public long ManagedHeapBytes { get; }

    /// <summary>An alias for the resident memory measurement.</summary>
    public long RamBytes => WorkingSetBytes;

    private static void EnsureNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>Logical-to-physical write cost observations for one measurement window.</summary>
public sealed record WriteAmplificationMetrics
{
    /// <summary>Creates a write amplification measurement.</summary>
    /// <param name="logicalBytes">The logical bytes accepted by the storage API.</param>
    /// <param name="physicalBytes">The physical bytes written by storage.</param>
    /// <param name="writeCount">The number of logical writes represented by the window.</param>
    public WriteAmplificationMetrics(long logicalBytes, long physicalBytes, long writeCount = 0)
    {
        EnsureNonNegative(logicalBytes, nameof(logicalBytes));
        EnsureNonNegative(physicalBytes, nameof(physicalBytes));
        EnsureNonNegative(writeCount, nameof(writeCount));
        LogicalBytes = logicalBytes;
        PhysicalBytes = physicalBytes;
        WriteCount = writeCount;
    }

    /// <summary>The logical bytes accepted by storage.</summary>
    public long LogicalBytes { get; }

    /// <summary>The physical bytes written by storage.</summary>
    public long PhysicalBytes { get; }

    /// <summary>The number of logical writes represented by the sample.</summary>
    public long WriteCount { get; }

    /// <summary>The physical-to-logical byte ratio. Empty windows report zero.</summary>
    public double Ratio => LogicalBytes == 0 ? 0 : (double)PhysicalBytes / LogicalBytes;

    private static void EnsureNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>Replication distance observations for one Region.</summary>
public sealed record ReplicationLagMetrics
{
    /// <summary>Creates a replication lag measurement.</summary>
    /// <param name="region">The Region represented by the observation.</param>
    /// <param name="sequenceLag">The number of storage sequences behind the source.</param>
    /// <param name="timeLag">The elapsed replication delay.</param>
    public ReplicationLagMetrics(Region region, long sequenceLag, TimeSpan timeLag)
    {
        EnsureNonNegative(sequenceLag, nameof(sequenceLag));
        if (timeLag < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeLag));
        }

        Region = region;
        SequenceLag = sequenceLag;
        TimeLag = timeLag;
    }

    /// <summary>The Region represented by the observation.</summary>
    public Region Region { get; }

    /// <summary>The number of storage sequences behind the source.</summary>
    public long SequenceLag { get; }

    /// <summary>The elapsed replication delay.</summary>
    public TimeSpan TimeLag { get; }

    /// <summary>An alias for the elapsed replication delay.</summary>
    public TimeSpan Lag => TimeLag;

    private static void EnsureNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>Outcome counts for one bounded fan-out operation.</summary>
public sealed record FanOutMetrics
{
    /// <summary>Creates a fan-out measurement.</summary>
    /// <param name="targetCount">The number of selected targets.</param>
    /// <param name="succeededCount">The number of successful target operations.</param>
    /// <param name="failedCount">The number of failed target operations.</param>
    /// <param name="cancelledCount">The number of cancelled target operations.</param>
    public FanOutMetrics(long targetCount, long succeededCount, long failedCount, long cancelledCount = 0)
    {
        EnsureNonNegative(targetCount, nameof(targetCount));
        EnsureNonNegative(succeededCount, nameof(succeededCount));
        EnsureNonNegative(failedCount, nameof(failedCount));
        EnsureNonNegative(cancelledCount, nameof(cancelledCount));
        if (succeededCount > targetCount || failedCount > targetCount - succeededCount ||
            cancelledCount > targetCount - succeededCount - failedCount)
        {
            throw new ArgumentException("Fan-out outcomes cannot exceed the target count.", nameof(targetCount));
        }

        TargetCount = targetCount;
        SucceededCount = succeededCount;
        FailedCount = failedCount;
        CancelledCount = cancelledCount;
    }

    /// <summary>The number of selected targets.</summary>
    public long TargetCount { get; }

    /// <summary>The number of successful target operations.</summary>
    public long SucceededCount { get; }

    /// <summary>The number of failed target operations.</summary>
    public long FailedCount { get; }

    /// <summary>The number of cancelled target operations.</summary>
    public long CancelledCount { get; }

    /// <summary>The number of target operations that have not completed.</summary>
    public long PendingCount => TargetCount - SucceededCount - FailedCount - CancelledCount;

    /// <summary>An alias for the selected target count.</summary>
    public long Targets => TargetCount;

    private static void EnsureNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>Restore workload observations, including elapsed restore time.</summary>
public record RestoreTimeMetrics
{
    /// <summary>Creates a restore measurement.</summary>
    /// <param name="duration">The elapsed restore duration.</param>
    /// <param name="bytesRestored">The number of restored bytes.</param>
    /// <param name="itemsRestored">The number of restored records or objects.</param>
    public RestoreTimeMetrics(TimeSpan duration, long bytesRestored = 0, long itemsRestored = 0)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        EnsureNonNegative(bytesRestored, nameof(bytesRestored));
        EnsureNonNegative(itemsRestored, nameof(itemsRestored));
        Duration = duration;
        BytesRestored = bytesRestored;
        ItemsRestored = itemsRestored;
    }

    /// <summary>The elapsed restore duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The number of restored bytes.</summary>
    public long BytesRestored { get; }

    /// <summary>The number of restored records or objects.</summary>
    public long ItemsRestored { get; }

    /// <summary>The elapsed restore duration in milliseconds.</summary>
    public double DurationMilliseconds => Duration.TotalMilliseconds;

    /// <summary>An alias for the elapsed restore duration.</summary>
    public TimeSpan RestoreTime => Duration;

    private static void EnsureNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>Short aliases for the names used by callers that treat restore as a metric family.</summary>
public sealed record RestoreMetrics(TimeSpan Duration, long BytesRestored = 0, long ItemsRestored = 0)
    : RestoreTimeMetrics(Duration, BytesRestored, ItemsRestored);

/// <summary>Stable names for the additional Prometheus-compatible metric families.</summary>
public static class ObservabilityMetricNames
{
    /// <summary>Request p50 latency, in seconds.</summary>
    public const string RequestLatencyP50Seconds = "terrabase_request_latency_p50_seconds";

    /// <summary>Request p95 latency, in seconds.</summary>
    public const string RequestLatencyP95Seconds = "terrabase_request_latency_p95_seconds";

    /// <summary>Request p99 latency, in seconds.</summary>
    public const string RequestLatencyP99Seconds = "terrabase_request_latency_p99_seconds";

    /// <summary>Allocated bytes.</summary>
    public const string AllocatedBytesTotal = "terrabase_allocated_bytes_total";

    /// <summary>Process CPU utilization, in percent.</summary>
    public const string CpuUtilizationPercent = "terrabase_cpu_utilization_percent";

    /// <summary>Resident process memory, in bytes.</summary>
    public const string RamBytes = "terrabase_ram_bytes";

    /// <summary>Physical-to-logical write amplification ratio.</summary>
    public const string WriteAmplificationRatio = "terrabase_write_amplification_ratio";

    /// <summary>Replication sequence lag.</summary>
    public const string ReplicationLagSequences = "terrabase_replication_lag_sequences";

    /// <summary>Replication time lag, in seconds.</summary>
    public const string ReplicationLagSeconds = "terrabase_replication_lag_seconds";

    /// <summary>Fan-out target count.</summary>
    public const string FanOutTargets = "terrabase_fan_out_targets";

    /// <summary>Restore duration, in seconds.</summary>
    public const string RestoreDurationSeconds = "terrabase_restore_duration_seconds";
}
