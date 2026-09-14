using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Reactive;

namespace TerraBase.Benchmarks;

/// <summary>Runs a bounded reactive subscription fan-out workload.</summary>
/// <remarks>
/// The commit callback must return the change set produced by the durable commit
/// and must not publish it to the hub. The harness publishes that set once after
/// the callback completes, which keeps the measured path explicit and prevents a
/// per-subscriber query execution from being mistaken for fan-out work.
/// </remarks>
public static class SubscriptionFanoutWorkload
{
    /// <summary>The subscriber counts used by the standard fan-out comparison.</summary>
    public static IReadOnlyList<int> DefaultSubscriberCounts { get; } = [1, 10, 100, 1_000];

    /// <summary>Runs one case for every configured subscriber count.</summary>
    public static async ValueTask<SubscriptionFanoutRun> RunAsync<TParameters, TResult>(
        SubscriptionFanoutScenario<TParameters, TResult> scenario,
        SubscriptionFanoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        options ??= new SubscriptionFanoutOptions();
        options.Validate();

        var startedAt = DateTimeOffset.UtcNow;
        var cases = new List<SubscriptionFanoutCaseResult>(options.SubscriberCounts.Count);
        if (!scenario.DurableCommitPathAvailable)
        {
            var reason = scenario.UnsupportedReason
                ?? "The durable commit-to-reactive source is not wired for this benchmark run.";
            foreach (var subscriberCount in options.SubscriberCounts)
            {
                cases.Add(UnsupportedCase(subscriberCount, reason));
            }

            return new SubscriptionFanoutRun(
                Status: "unsupported",
                Subject: "ReactiveSubscriptionHub",
                OperationBoundary: "durable commit -> one shared query maintenance -> subscriber frame delivery",
                DurableCommitPathAvailable: false,
                StartedAtUtc: startedAt,
                FinishedAtUtc: DateTimeOffset.UtcNow,
                UnsupportedReason: reason,
                Cases: cases);
        }

        if (scenario.CommitAsync is null)
        {
            const string reason = "No durable commit callback was supplied.";
            foreach (var subscriberCount in options.SubscriberCounts)
            {
                cases.Add(UnsupportedCase(subscriberCount, reason));
            }

            return new SubscriptionFanoutRun(
                Status: "unsupported",
                Subject: "ReactiveSubscriptionHub",
                OperationBoundary: "durable commit -> one shared query maintenance -> subscriber frame delivery",
                DurableCommitPathAvailable: false,
                StartedAtUtc: startedAt,
                FinishedAtUtc: DateTimeOffset.UtcNow,
                UnsupportedReason: reason,
                Cases: cases);
        }

        foreach (var subscriberCount in options.SubscriberCounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cases.Add(await RunCaseAsync(scenario, options, subscriberCount, cancellationToken).ConfigureAwait(false));
        }

        var status = cases.Any(static result => result.Status == "failed")
            ? "failed"
            : cases.All(static result => result.Status == "complete")
                ? "complete"
                : "unsupported";
        return new SubscriptionFanoutRun(
            Status: status,
            Subject: "ReactiveSubscriptionHub",
            OperationBoundary: "durable commit -> one shared query maintenance -> subscriber frame delivery",
            DurableCommitPathAvailable: true,
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTimeOffset.UtcNow,
            UnsupportedReason: null,
            Cases: cases);
    }

    private static async ValueTask<SubscriptionFanoutCaseResult> RunCaseAsync<TParameters, TResult>(
        SubscriptionFanoutScenario<TParameters, TResult> scenario,
        SubscriptionFanoutOptions options,
        int subscriberCount,
        CancellationToken cancellationToken)
    {
        var probe = scenario.Probe;
        probe?.Reset();
        var subscriptions = new List<ReactiveSubscription<TResult>>(subscriberCount);
        var slowSubscriberIndex = options.ExerciseSlowSubscriber && subscriberCount > 1 ? 0 : -1;
        string? slowCheckpoint = null;
        var startedAt = DateTimeOffset.UtcNow;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(false);
        var workingSetBefore = process.WorkingSet64;
        var deliveryLatencies = new List<double>(subscriberCount * options.MeasuredPublications);
        long egressBytes = 0;
        long deliveredFrames = 0;
        long incrementalUpdates = 0;
        long changedDocuments = 0;
        long backpressureResets = 0;
        long backpressureDisconnects = 0;
        string? reconnectFrameKind = null;
        long reconnectBytes = 0;

        var query = new ReactiveQuery<TParameters, TResult>(
            scenario.Operation,
            async (parameters, context, token) =>
            {
                probe?.RecordOriginEvaluation();
                return await scenario.ExecuteAsync(parameters, context, token).ConfigureAwait(false);
            },
            scenario.Dependencies);

        try
        {
            for (var index = 0; index < subscriberCount; index++)
            {
                var delivery = index == slowSubscriberIndex
                    ? new ReactiveOptions
                    {
                        SubscriberBufferSize = options.SlowSubscriberBufferSize,
                        Backpressure = options.SlowSubscriberBackpressure,
                        RetainedRevisions = options.RetainedRevisions,
                    }
                    : new ReactiveOptions
                    {
                        SubscriberBufferSize = options.SubscriberBufferSize,
                        Backpressure = options.Backpressure,
                        RetainedRevisions = options.RetainedRevisions,
                    };
                var subscription = await scenario.Hub.SubscribeAsync(
                    query,
                    scenario.Parameters,
                    scenario.Authorization,
                    new ReactiveSubscriptionOptions { Delivery = delivery },
                    cancellationToken).ConfigureAwait(false);
                subscriptions.Add(subscription);
                var snapshot = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (snapshot.Kind != ReactiveFrameKind.Snapshot)
                {
                    throw new InvalidOperationException($"Subscriber {index} did not receive an initial snapshot ({snapshot.Kind}).");
                }

                if (index == slowSubscriberIndex)
                {
                    slowCheckpoint = snapshot.Checkpoint;
                }
            }

            for (var index = 0; index < options.WarmupPublications; index++)
            {
                var frames = await PublishAndReadAsync(
                    scenario,
                    subscriptions,
                    options,
                    slowSubscriberIndex: -1,
                    cancellationToken).ConfigureAwait(false);
                EnsureDeltaFrames(frames, "warmup");
            }

            probe?.Reset();
            process.Refresh();
            cpuBefore = process.TotalProcessorTime;
            allocationsBefore = GC.GetTotalAllocatedBytes(false);
            workingSetBefore = process.WorkingSet64;

            for (var index = 0; index < options.MeasuredPublications; index++)
            {
                var measured = await PublishAndReadAsync(
                    scenario,
                    subscriptions,
                    options,
                    slowSubscriberIndex,
                    cancellationToken).ConfigureAwait(false);
                var responsiveFrames = measured
                    .Where(observation => observation.SubscriberIndex != slowSubscriberIndex)
                    .ToArray();
                EnsureDeltaFrames(responsiveFrames, "measured");
                if (responsiveFrames.Length != 0)
                {
                    incrementalUpdates++;
                    changedDocuments += responsiveFrames[0].Frame.Changes.Count;
                }

                foreach (var observation in responsiveFrames)
                {
                    deliveredFrames++;
                    deliveryLatencies.Add(observation.LatencyMicroseconds);
                    egressBytes += observation.SerializedBytes;
                }
            }

            if (slowSubscriberIndex >= 0)
            {
                try
                {
                    var slowFrame = await ReadFrameAsync(
                        subscriptions[slowSubscriberIndex],
                        slowSubscriberIndex,
                        Stopwatch.GetTimestamp(),
                        options.DeliveryTimeout,
                        cancellationToken).ConfigureAwait(false);
                    egressBytes += slowFrame.SerializedBytes;
                    if (slowFrame.Frame.Kind == ReactiveFrameKind.Backpressure)
                    {
                        backpressureResets++;
                    }
                }
                catch (ReactiveBackpressureException)
                {
                    backpressureDisconnects++;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A slow subscriber with no queued frame is an observed absence
                    // of delivery, not an invented zero-latency measurement.
                }

                if (slowCheckpoint is not null)
                {
                    await subscriptions[slowSubscriberIndex].DisposeAsync().ConfigureAwait(false);
                    subscriptions[slowSubscriberIndex] = await scenario.Hub.SubscribeAsync(
                        query,
                        scenario.Parameters,
                        scenario.Authorization,
                        new ReactiveSubscriptionOptions
                        {
                            ResumeFrom = slowCheckpoint,
                            Delivery = new ReactiveOptions
                            {
                                SubscriberBufferSize = options.SubscriberBufferSize,
                                Backpressure = options.Backpressure,
                                RetainedRevisions = options.RetainedRevisions,
                            },
                        },
                        cancellationToken).ConfigureAwait(false);
                    var reconnect = await subscriptions[slowSubscriberIndex].ReadAsync(cancellationToken).ConfigureAwait(false);
                    reconnectFrameKind = reconnect.Kind.ToString();
                    reconnectBytes = JsonSerializer.SerializeToUtf8Bytes(reconnect).LongLength;
                }
            }

            process.Refresh();
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var cpu = process.TotalProcessorTime - cpuBefore;
            var allocations = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocationsBefore);
            var latency = Percentiles(deliveryLatencies);
            return new SubscriptionFanoutCaseResult(
                Subscribers: subscriberCount,
                ResponsiveSubscribers: subscriberCount - (slowSubscriberIndex >= 0 ? 1 : 0),
                Status: "complete",
                OriginEvaluations: probe?.OriginEvaluations,
                IncrementalUpdates: incrementalUpdates,
                ChangedDocuments: changedDocuments,
                DeliveredFrames: deliveredFrames,
                EgressBytes: egressBytes,
                AllocatedBytes: allocations,
                AllocatedBytesPerDeliveredFrame: deliveredFrames == 0 ? null : allocations / (double)deliveredFrames,
                ProcessCpuMilliseconds: cpu.TotalMilliseconds,
                ProcessWorkingSetBytesBefore: workingSetBefore,
                ProcessWorkingSetBytesAfter: process.WorkingSet64,
                ProcessWorkingSetDeltaBytes: process.WorkingSet64 - workingSetBefore,
                ElapsedMilliseconds: elapsed.TotalMilliseconds,
                P50DeliveryLatencyMicroseconds: latency.P50,
                P95DeliveryLatencyMicroseconds: latency.P95,
                P99DeliveryLatencyMicroseconds: latency.P99,
                BackpressureResets: backpressureResets,
                BackpressureDisconnects: backpressureDisconnects,
                ReconnectFrameKind: reconnectFrameKind,
                ReconnectResetObserved: string.Equals(reconnectFrameKind, ReactiveFrameKind.Reset.ToString(), StringComparison.Ordinal),
                ReconnectEgressBytes: reconnectBytes,
                Error: null,
                UnsupportedReason: null);
        }
        catch (PlatformNotSupportedException exception)
        {
            return UnsupportedCase(subscriberCount, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return UnsupportedCase(subscriberCount, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SubscriptionFanoutCaseResult(
                Subscribers: subscriberCount,
                ResponsiveSubscribers: null,
                Status: "failed",
                OriginEvaluations: probe?.OriginEvaluations,
                IncrementalUpdates: null,
                ChangedDocuments: null,
                DeliveredFrames: deliveredFrames == 0 ? null : deliveredFrames,
                EgressBytes: egressBytes == 0 ? null : egressBytes,
                AllocatedBytes: null,
                AllocatedBytesPerDeliveredFrame: null,
                ProcessCpuMilliseconds: null,
                ProcessWorkingSetBytesBefore: null,
                ProcessWorkingSetBytesAfter: null,
                ProcessWorkingSetDeltaBytes: null,
                ElapsedMilliseconds: null,
                P50DeliveryLatencyMicroseconds: null,
                P95DeliveryLatencyMicroseconds: null,
                P99DeliveryLatencyMicroseconds: null,
                BackpressureResets: null,
                BackpressureDisconnects: null,
                ReconnectFrameKind: null,
                ReconnectResetObserved: null,
                ReconnectEgressBytes: null,
                Error: exception.ToString(),
                UnsupportedReason: null);
        }
        finally
        {
            for (var index = subscriptions.Count - 1; index >= 0; index--)
            {
                await subscriptions[index].DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<FrameObservation<TResult>[]> PublishAndReadAsync<TParameters, TResult>(
        SubscriptionFanoutScenario<TParameters, TResult> scenario,
        IReadOnlyList<ReactiveSubscription<TResult>> subscriptions,
        SubscriptionFanoutOptions options,
        int slowSubscriberIndex,
        CancellationToken cancellationToken)
    {
        var readStart = Stopwatch.GetTimestamp();
        var responsive = Enumerable.Range(0, subscriptions.Count)
            .Where(index => index != slowSubscriberIndex)
            .Select(index => ReadFrameAsync(
                subscriptions[index],
                index,
                readStart,
                options.DeliveryTimeout,
                cancellationToken))
            .ToArray();
        var changeSet = await scenario.CommitAsync!(cancellationToken).ConfigureAwait(false);
        await scenario.Hub.PublishAsync(changeSet, cancellationToken).ConfigureAwait(false);
        return await Task.WhenAll(responsive).ConfigureAwait(false);
    }

    private static async Task<FrameObservation<TResult>> ReadFrameAsync<TResult>(
        ReactiveSubscription<TResult> subscription,
        int subscriberIndex,
        long started,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var frame = await subscription.ReadAsync(timeoutCancellation.Token).ConfigureAwait(false);
        var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame).LongLength;
        return new FrameObservation<TResult>(subscriberIndex, frame, latency, bytes);
    }

    private static void EnsureDeltaFrames<TResult>(IReadOnlyList<FrameObservation<TResult>> observations, string phase)
    {
        foreach (var observation in observations)
        {
            if (observation.Frame.Kind != ReactiveFrameKind.Delta)
            {
                throw new InvalidOperationException(
                    $"The {phase} publication delivered {observation.Frame.Kind} to subscriber {observation.SubscriberIndex}; a changed shared query must deliver a delta.");
            }
        }
    }

    private static SubscriptionFanoutCaseResult UnsupportedCase(int subscriberCount, string reason) =>
        new(
            Subscribers: subscriberCount,
            ResponsiveSubscribers: null,
            Status: "unsupported",
            OriginEvaluations: null,
            IncrementalUpdates: null,
            ChangedDocuments: null,
            DeliveredFrames: null,
            EgressBytes: null,
            AllocatedBytes: null,
            AllocatedBytesPerDeliveredFrame: null,
            ProcessCpuMilliseconds: null,
            ProcessWorkingSetBytesBefore: null,
            ProcessWorkingSetBytesAfter: null,
            ProcessWorkingSetDeltaBytes: null,
            ElapsedMilliseconds: null,
            P50DeliveryLatencyMicroseconds: null,
            P95DeliveryLatencyMicroseconds: null,
            P99DeliveryLatencyMicroseconds: null,
            BackpressureResets: null,
            BackpressureDisconnects: null,
            ReconnectFrameKind: null,
            ReconnectResetObserved: null,
            ReconnectEgressBytes: null,
            Error: null,
            UnsupportedReason: reason);

    private static LatencyPercentiles Percentiles(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return default;
        }

        var ordered = values.OrderBy(static value => value).ToArray();
        return new(
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99));
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 1)
        {
            return values[0];
        }

        var position = (values.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return values[lower];
        }

        var fraction = position - lower;
        return values[lower] + (values[upper] - values[lower]) * fraction;
    }

    private readonly record struct FrameObservation<TResult>(
        int SubscriberIndex,
        ReactiveFrame<TResult> Frame,
        double LatencyMicroseconds,
        long SerializedBytes);

    private readonly record struct LatencyPercentiles(double P50, double P95, double P99);
}

/// <summary>Inputs to one reactive fan-out run.</summary>
public sealed record SubscriptionFanoutScenario<TParameters, TResult>
{
    /// <summary>Creates a fan-out scenario.</summary>
    public SubscriptionFanoutScenario(
        ReactiveSubscriptionHub hub,
        ReactiveOperationId operation,
        TParameters parameters,
        ReactiveAuthorization authorization,
        Func<TParameters, ReactiveQueryContext, CancellationToken, ValueTask<TResult>> executeAsync,
        Func<TParameters, ReactiveDependencyCollection>? dependencies,
        Func<CancellationToken, ValueTask<ReactiveChangeSet>>? commitAsync,
        SubscriptionFanoutProbe? probe = null,
        bool durableCommitPathAvailable = false,
        string? unsupportedReason = null)
    {
        Hub = hub ?? throw new ArgumentNullException(nameof(hub));
        Operation = operation;
        Parameters = parameters;
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        Dependencies = dependencies;
        CommitAsync = commitAsync;
        Probe = probe;
        DurableCommitPathAvailable = durableCommitPathAvailable;
        UnsupportedReason = unsupportedReason;
    }

    /// <summary>The shared subscription hub under test.</summary>
    public ReactiveSubscriptionHub Hub { get; }

    /// <summary>The stable operation identity used by every subscriber.</summary>
    public ReactiveOperationId Operation { get; }

    /// <summary>The equivalent query parameters used by every subscriber.</summary>
    public TParameters Parameters { get; }

    /// <summary>The authorization scope shared by every subscriber.</summary>
    public ReactiveAuthorization Authorization { get; }

    /// <summary>The query body used to build one shared query instance.</summary>
    public Func<TParameters, ReactiveQueryContext, CancellationToken, ValueTask<TResult>> ExecuteAsync { get; }

    /// <summary>Optional static dependencies for the query.</summary>
    public Func<TParameters, ReactiveDependencyCollection>? Dependencies { get; }

    /// <summary>Returns a committed change set without publishing it.</summary>
    public Func<CancellationToken, ValueTask<ReactiveChangeSet>>? CommitAsync { get; }

    /// <summary>Optional instrumentation for origin query evaluations.</summary>
    public SubscriptionFanoutProbe? Probe { get; }

    /// <summary>Whether the callback is backed by the real durable commit path.</summary>
    public bool DurableCommitPathAvailable { get; }

    /// <summary>Reason to report when the durable path is unavailable.</summary>
    public string? UnsupportedReason { get; }
}

/// <summary>Configuration for the subscription fan-out workload.</summary>
public sealed record SubscriptionFanoutOptions
{
    /// <summary>Subscriber counts to measure, in ascending order.</summary>
    public IReadOnlyList<int> SubscriberCounts { get; init; } = SubscriptionFanoutWorkload.DefaultSubscriberCounts;

    /// <summary>Warm-up publications excluded from measurements.</summary>
    public int WarmupPublications { get; init; } = 2;

    /// <summary>Publications measured per subscriber count.</summary>
    public int MeasuredPublications { get; init; } = 5;

    /// <summary>Normal subscriber queue capacity.</summary>
    public int SubscriberBufferSize { get; init; } = 32;

    /// <summary>Normal subscriber backpressure behavior.</summary>
    public ReactiveBackpressureMode Backpressure { get; init; } = ReactiveBackpressureMode.Reset;

    /// <summary>Retained revisions used by each subscription delivery policy.</summary>
    public int RetainedRevisions { get; init; } = 256;

    /// <summary>Queue capacity assigned to the deliberately slow subscriber.</summary>
    public int SlowSubscriberBufferSize { get; init; } = 1;

    /// <summary>Backpressure behavior assigned to the deliberately slow subscriber.</summary>
    public ReactiveBackpressureMode SlowSubscriberBackpressure { get; init; } = ReactiveBackpressureMode.Reset;

    /// <summary>Whether to hold one subscriber back to exercise bounded delivery.</summary>
    public bool ExerciseSlowSubscriber { get; init; } = true;

    /// <summary>Maximum wait for one responsive subscriber frame.</summary>
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(SubscriberCounts);
        if (SubscriberCounts.Count == 0 || SubscriberCounts.Any(static value => value < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(SubscriberCounts), "At least one positive subscriber count is required.");
        }

        if (SubscriberCounts.Distinct().Count() != SubscriberCounts.Count)
        {
            throw new ArgumentException("Subscriber counts must be unique.", nameof(SubscriberCounts));
        }

        if (WarmupPublications < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WarmupPublications));
        }

        if (MeasuredPublications < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MeasuredPublications));
        }

        if (SubscriberBufferSize < 1 || SubscriberBufferSize > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(SubscriberBufferSize));
        }

        if (RetainedRevisions < 1 || RetainedRevisions > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(RetainedRevisions));
        }

        if (SlowSubscriberBufferSize < 1 || SlowSubscriberBufferSize > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(SlowSubscriberBufferSize));
        }

        if (DeliveryTimeout <= TimeSpan.Zero || DeliveryTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(DeliveryTimeout));
        }
    }
}

/// <summary>Optional counters supplied by a query delegate.</summary>
public sealed class SubscriptionFanoutProbe
{
    private long originEvaluations;

    /// <summary>The number of times the shared query body was evaluated.</summary>
    public long OriginEvaluations => Interlocked.Read(ref originEvaluations);

    /// <summary>Records one query evaluation at the origin.</summary>
    public void RecordOriginEvaluation() => Interlocked.Increment(ref originEvaluations);

    internal void Reset() => Interlocked.Exchange(ref originEvaluations, 0);
}

/// <summary>Machine-readable result for one subscription fan-out run.</summary>
public sealed record SubscriptionFanoutRun(
    string Status,
    string Subject,
    string OperationBoundary,
    bool DurableCommitPathAvailable,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string? UnsupportedReason,
    IReadOnlyList<SubscriptionFanoutCaseResult> Cases);

/// <summary>Machine-readable measurements for one subscriber count.</summary>
public sealed record SubscriptionFanoutCaseResult(
    int Subscribers,
    int? ResponsiveSubscribers,
    string Status,
    long? OriginEvaluations,
    long? IncrementalUpdates,
    long? ChangedDocuments,
    long? DeliveredFrames,
    long? EgressBytes,
    long? AllocatedBytes,
    double? AllocatedBytesPerDeliveredFrame,
    double? ProcessCpuMilliseconds,
    long? ProcessWorkingSetBytesBefore,
    long? ProcessWorkingSetBytesAfter,
    long? ProcessWorkingSetDeltaBytes,
    double? ElapsedMilliseconds,
    double? P50DeliveryLatencyMicroseconds,
    double? P95DeliveryLatencyMicroseconds,
    double? P99DeliveryLatencyMicroseconds,
    long? BackpressureResets,
    long? BackpressureDisconnects,
    string? ReconnectFrameKind,
    bool? ReconnectResetObserved,
    long? ReconnectEgressBytes,
    string? Error,
    string? UnsupportedReason);
