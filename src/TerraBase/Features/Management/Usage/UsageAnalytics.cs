using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed class UsageAnalytics(IManagementService management, UsageMeter meter, TerraBaseOptions options, TimeProvider time, IMemoryBudget memory)
    : IUsageAnalytics
{
    private const int MaxPoints = 16384;
    // Health sources refresh every ten seconds; a peer silent past Fresh is stopped, and past Forget it was removed.
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Forget = TimeSpan.FromMinutes(10);

    public ValueTask<AnalyticsRead<ProjectAnalytics>> AnalyticsAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken) =>
        BoundedAsync(bounded => ReadAnalyticsAsync(access, window, database, region, retain, bounded), cancellationToken);

    public ValueTask<AnalyticsRead<ProjectReplication>> ReplicationAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken) =>
        BoundedAsync(bounded => ReadReplicationAsync(access, window, database, region, retain, bounded), cancellationToken);

    // Runs a read within the usage deadline; the read's capacity and deadline failures become cases.
    private static async ValueTask<AnalyticsRead<T>> BoundedAsync<T>(Func<CancellationToken, ValueTask<AnalyticsRead<T>>> read, CancellationToken cancellationToken)
        where T : class
    {
        using var deadline = new CancellationTokenSource(UsageService.Deadline);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await read(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Unavailable("analytics exceeded its execution budget", "capacity");
        }
        catch (QueryCapacityException)
        {
            return new Validation("query too expensive", "invalid_query");
        }
    }

    private async ValueTask<AnalyticsRead<ProjectAnalytics>> ReadAnalyticsAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var listed = await DatabasesAsync(access, database, started, cancellationToken).ConfigureAwait(false);
        if (listed is not List<ManagedDatabase> databases)
        {
            return listed switch
            {
                NotFound error => error,
                Unavailable error => error,
                List<ManagedDatabase> or null => throw new UnreachableException(),
            };
        }

        var (reservation, reserved) = Reserve(databases, window, region);
        using var loaded = new Loaded(memory, reserved, reservation);
        var items = new List<DatabaseAnalytics>();
        foreach (var record in databases)
        {
            CheckDeadline(started);
            var found = await management.GetDatabaseTenantAsync(access.Actor, access.OrganizationId, access.ProjectId, record.Id, cancellationToken).ConfigureAwait(false);
            if (found is Unavailable refused)
            {
                return refused;
            }
            if (found is not TenantContext tenant)
            {
                continue;
            }

            var observed = await meter.GetObservationsAsync(tenant, window.Usage, cancellationToken, () => CheckDeadline(started), loaded.Filter(region))
                .ConfigureAwait(false);
            if (observed is not UsageCounts counts)
            {
                return observed switch
                {
                    Unavailable error => error,
                    UsageCounts or null => throw new UnreachableException(),
                };
            }
            var (lifetime, series) = counts;
            var buckets = ByRegion(series, started, cancellationToken);
            foreach (var observing in Regions(record, lifetime, region, window, loaded))
            {
                items.Add(new(record.Id, record.Name, observing, [.. buckets.Select((point, index) =>
                {
                    Check(started, cancellationToken);
                    return Point(window.Usage.Granularity.Start(window.Usage.First + index), point[observing]);
                })]));
            }
        }

        CheckDeadline(started);
        return loaded.HandOff(retain, new ProjectAnalytics(access.ProjectId, window.Range, window.Usage.From, window.Usage.To, window.Usage.Granularity.Minutes * 60, items));
    }

    private async ValueTask<AnalyticsRead<ProjectReplication>> ReadReplicationAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var listed = await DatabasesAsync(access, database, started, cancellationToken).ConfigureAwait(false);
        if (listed is not List<ManagedDatabase> databases)
        {
            return listed switch
            {
                NotFound error => error,
                Unavailable error => error,
                List<ManagedDatabase> or null => throw new UnreachableException(),
            };
        }

        var (reservation, reserved) = Reserve(databases, window, region);
        using var loaded = new Loaded(memory, reserved, reservation);
        var items = new List<DatabaseReplication>();
        foreach (var record in databases)
        {
            CheckDeadline(started);
            var found = await management.GetDatabaseTenantAsync(access.Actor, access.OrganizationId, access.ProjectId, record.Id, cancellationToken).ConfigureAwait(false);
            if (found is Unavailable refused)
            {
                return refused;
            }
            if (found is not TenantContext tenant)
            {
                continue;
            }

            var observed = await meter.GetObservationsAsync(tenant, window.Usage, cancellationToken, () => CheckDeadline(started), loaded.Filter(region))
                .ConfigureAwait(false);
            if (observed is not UsageCounts counts)
            {
                return observed switch
                {
                    Unavailable error => error,
                    UsageCounts or null => throw new UnreachableException(),
                };
            }
            var (lifetime, series) = counts;
            var buckets = ByRegion(series, started, cancellationToken);
            foreach (var observing in Regions(record, lifetime, region, window, loaded))
            {
                Check(started, cancellationToken);
                var readings = Readings(lifetime, observing).ToArray();
                // Every peer observed within Forget counts; one fresh healthy peer cannot hide a silent or diverged one.
                var now = time.GetUtcNow();
                var health = readings.Where(reading => reading.Dimension.Meter == "replication_status" && reading.State is not null)
                    .Select(reading => reading.State!).Where(state => now - state.ObservedAt <= Forget).ToArray();
                var recent = health.Where(state => now - state.ObservedAt <= Fresh).ToArray();
                var applied = readings.Where(reading => reading.Dimension.Meter == "replicated_ops").Select(reading => reading.State?.LastApplied).Max();
                var lag = recent.Length == 0 ? (double?)null : recent.Max(state => state.LagMs);
                // Only fresh observations are current: a silent peer is stopped, not diverged, even beside a fresh one.
                var diverged = recent.Length == 0 ? (bool?)null : recent.Any(state => state.Diverged);
                var status = diverged == true ? "diverged" : recent.Length < health.Length || recent.Length == 0 ? "stopped"
                    : recent.Any(state => state.Remaining > 0) ? "lagging" : "healthy";
                var points = buckets.Select((point, index) =>
                {
                    Check(started, cancellationToken);
                    var values = point[observing].ToArray();
                    return new ReplicationPoint(window.Usage.Granularity.Start(window.Usage.First + index),
                        values.Where(reading => reading.Dimension.Meter == "replicated_ops").Saturated(reading => reading.Count), Bandwidth(values));
                }).ToArray();
                items.Add(new(record.Id, record.Name, observing, status, points.Saturated(point => point.ReplicatedOps), lag, diverged,
                    health.Select(state => state.ConvergenceMs).Max(), applied, Bandwidth(buckets.SelectMany(point => point[observing])), points));
            }
        }

        CheckDeadline(started);
        return loaded.HandOff(retain, new ProjectReplication(access.ProjectId, window.Range, window.Usage.From, window.Usage.To, window.Usage.Granularity.Minutes * 60, items));
    }

    private readonly union AnalyticsDatabases(List<ManagedDatabase>, NotFound, Unavailable);

    private async ValueTask<AnalyticsDatabases> DatabasesAsync(ProjectAccess access, string? database, long started, CancellationToken cancellationToken)
    {
        if (database is not null)
        {
            if (access.DatabaseName is not null && database != access.DatabaseId)
            {
                return new NotFound();
            }

            return await management.GetDatabaseAsync(access.Actor, access.OrganizationId, access.ProjectId, database, cancellationToken).ConfigureAwait(false) switch
            {
                ManagedDatabase found => new List<ManagedDatabase> { found },
                NotFound error => error,
                Unavailable error => error,
            };
        }

        List<ManagedDatabase> result = [];
        string? after = null;
        do
        {
            CheckDeadline(started);
            var listed = await management.ListDatabasesAsync(access.Actor, access.OrganizationId, access.ProjectId, after, 100, cancellationToken).ConfigureAwait(false);
            if (listed is not ManagementPage<ManagedDatabase> page)
            {
                return listed switch
                {
                    NotFound error => error,
                    Unavailable error => error,
                    ManagementPage<ManagedDatabase> or null => throw new UnreachableException(),
                };
            }

            result.AddRange(page.Items);
            if (result.Count > UsageService.MaxDatabases)
            {
                throw new QueryCapacityException("analytics covers more databases than one request reads");
            }

            after = page.Next;
        }
        while (after is not null);

        return result;
    }

    private (IMemoryReservation Reservation, int Points) Reserve(IReadOnlyList<ManagedDatabase> databases, AnalyticsWindow window, string? region)
    {
        // The local item of each database placed here; bounded extra room for two catalog read pages. Every other region a
        // database has observations for is admitted as it is found (Loaded.Items).
        var points = databases.Sum(record => Placed(record, region)) * window.Usage.Count;
        if (points > MaxPoints)
        {
            throw new QueryCapacityException("analytics exceeds its result bucket limit");
        }

        return (memory.TryReserve(MemoryLane.Foreground, 16L * 1024 * 1024 + points * 2048L)
            ?? throw new QueryCapacityException("analytics memory admission exhausted"), points);
    }

    // The item every request emits for a database: its local region while placed here. Observed regions are admitted as found.
    private int Placed(ManagedDatabase record, string? region) =>
        record.Regions(options.Region).Contains(options.Region) && (region is null || region == options.Region) ? 1 : 0;

    // Memory admitted for a request: its output reservation, extra output items and the observations it keeps. A returned result
    // hands all of it to the caller, who holds it until the result has been sent; otherwise it is released here.
    private sealed class Loaded(IMemoryBudget memory, int reserved, IMemoryReservation output) : IDisposable
    {
        private readonly List<IMemoryReservation> _reservations = [output];
        private int _points = reserved;
        private bool _handedOff;

        public T HandOff<T>(Action<IDisposable> retain, T result)
        {
            foreach (var reservation in _reservations)
            {
                retain(reservation);
            }

            _handedOff = true;
            return result;
        }

        // Admits output items beyond those reserved for a database's current placement.
        public void Items(int items, AnalyticsWindow window)
        {
            if (items <= 0)
            {
                return;
            }

            _points += items * window.Usage.Count;
            if (_points > MaxPoints)
            {
                throw new QueryCapacityException("analytics exceeds its result bucket limit");
            }

            _reservations.Add(Reserve(items * window.Usage.Count * 2048L));
        }

        public ObservationFilter Filter(string? region) => new(dimension => region is null || dimension.Region == region, bytes =>
        {
            if (bytes > 0)
            {
                _reservations.Add(Reserve(bytes));
            }
        }, Reserve);

        private IMemoryReservation Reserve(long bytes) =>
            memory.TryReserve(MemoryLane.Foreground, bytes) ?? throw new QueryCapacityException("analytics memory admission exhausted");

        public void Dispose()
        {
            if (_handedOff)
            {
                return;
            }

            foreach (var reservation in _reservations)
            {
                reservation.Dispose();
            }
        }
    }

    // The local region while placed here, and every region with observations, including one no longer in the placement: its
    // history remains readable. Unobserved remote placements are omitted.
    private string[] Regions(ManagedDatabase record, OpCounts lifetime, string? region, AnalyticsWindow window, Loaded loaded)
    {
        var regions = record.Regions(options.Region).Where(observing => observing == options.Region)
            .Concat(lifetime.Meters?.Values.Select(reading => reading.Dimension.Region) ?? [])
            .Distinct(StringComparer.Ordinal).Where(observing => region is null || observing == region).Order(StringComparer.Ordinal).ToArray();
        loaded.Items(regions.Length - Placed(record, region), window);
        return regions;
    }

    private void CheckDeadline(long started)
    {
        if (time.GetElapsedTime(started) >= UsageService.Deadline)
        {
            // Answered like the token deadline: a transient Unavailable, not an invalid query.
            throw new OperationCanceledException("analytics exceeded its execution budget");
        }
    }

    // Each bucket's readings by observing region, grouped once rather than rescanned per region.
    private ILookup<string, UsageReading>[] ByRegion(OpCounts[] series, long started, CancellationToken cancellationToken) =>
        [.. series.Select(point =>
        {
            Check(started, cancellationToken);
            IEnumerable<UsageReading> readings = point.Meters?.Values ?? [];
            return readings.ToLookup(reading => reading.Dimension.Region, StringComparer.Ordinal);
        })];

    private void Check(long started, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckDeadline(started);
    }

    private static IEnumerable<UsageReading> Readings(OpCounts counts, string region) =>
        counts.Meters?.Values.Where(reading => reading.Dimension.Region == region) ?? [];

    private static AnalyticsPoint Point(DateTimeOffset start, IEnumerable<UsageReading> readings)
    {
        var values = readings.ToArray();
        var requests = values.Where(reading => reading.Dimension.Meter == "requests").ToArray();
        var storage = values.Where(reading => reading.Dimension.Meter == "storage_bytes").ToArray();
        var samples = storage.Saturated(reading => reading.Count);
        return new(start, ByKind(requests), ByKind(values.Where(reading => reading.Dimension.Meter == "ops")), requests.Saturated(reading => reading.Value),
            Latency(requests),
            samples == 0 ? null : (long)(storage.Aggregate(Int128.Zero, (total, reading) => total + reading.Value) / samples),
            Traffic(values, "client_bytes"), Traffic(values, "replication_bytes"),
            values.Where(reading => reading.Dimension.Meter == "statements").GroupBy(reading => reading.Dimension.Kind).ToDictionary(group => group.Key,
                group => new StatementUsage(group.Saturated(reading => reading.Count), group.Saturated(reading => reading.Value), Latency(group)),
                StringComparer.Ordinal));
    }

    private static LatencyPercentiles Latency(IEnumerable<UsageReading> readings)
    {
        // Bins sum saturated readings; Int128 holds any merge without wrapping.
        var histogram = new Int128[UsageReading.LatencyBounds.Count];
        foreach (var reading in readings)
        {
            for (var i = 0; i < histogram.Length; i++)
            {
                histogram[i] += reading.Latency?[i] ?? 0;
            }
        }

        return new(Percentile(histogram, 0.5), Percentile(histogram, 0.95), Percentile(histogram, 0.99));
    }

    private static Dictionary<string, long> ByKind(IEnumerable<UsageReading> readings) =>
        readings.GroupBy(reading => reading.Dimension.Kind).ToDictionary(group => group.Key, group => group.Saturated(reading => reading.Count), StringComparer.Ordinal);

    private static BandwidthUsage Traffic(IEnumerable<UsageReading> readings, string meter)
    {
        var values = readings.Where(reading => reading.Dimension.Meter == meter).ToArray();
        return new(values.Where(reading => reading.Dimension.Kind.EndsWith("sent", StringComparison.Ordinal)).Saturated(reading => reading.Count),
            values.Where(reading => reading.Dimension.Kind.EndsWith("received", StringComparison.Ordinal)).Saturated(reading => reading.Count));
    }

    private static ReplicationBandwidth[] Bandwidth(IEnumerable<UsageReading> readings) =>
        [.. readings.Where(reading => reading.Dimension.Meter == "replication_bytes").GroupBy(reading =>
        {
            var dimension = reading.Dimension;
            var sent = dimension.Kind.EndsWith(":sent", StringComparison.Ordinal);
            return (Source: sent ? dimension.Region : dimension.PeerRegion, Destination: sent ? dimension.PeerRegion : dimension.Region,
                Kind: dimension.Kind.Split(':')[0]);
        }).Select(group => new ReplicationBandwidth(group.Key.Source, group.Key.Destination, group.Key.Kind,
            group.Where(reading => reading.Dimension.Kind.EndsWith(":sent", StringComparison.Ordinal)).Saturated(reading => reading.Count),
            group.Where(reading => reading.Dimension.Kind.EndsWith(":received", StringComparison.Ordinal)).Saturated(reading => reading.Count)))
        .OrderBy(value => value.SourceRegion, StringComparer.Ordinal).ThenBy(value => value.DestinationRegion, StringComparer.Ordinal)
        .ThenBy(value => value.Kind, StringComparer.Ordinal)];

    // As Prometheus' histogram_quantile: linear within the bin that holds the rank, from the bound below it (0 for the first
    // bin) to its own. The last bin also holds every longer request, so a rank there is its lower bound.
    private static double? Percentile(Int128[] histogram, double fraction)
    {
        var count = histogram.Aggregate(Int128.Zero, (total, bin) => total + bin);
        if (count == 0)
        {
            return null;
        }

        var rank = (double)count * fraction;
        Int128 seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] == 0 || (double)(seen + histogram[i]) < rank)
            {
                seen += histogram[i];
                continue;
            }

            var lower = i == 0 ? 0 : UsageReading.LatencyBounds[i - 1];
            return i == histogram.Length - 1 ? lower
                : lower + (UsageReading.LatencyBounds[i] - lower) * Math.Clamp((rank - (double)seen) / (double)histogram[i], 0, 1);
        }

        return UsageReading.LatencyBounds[^2];
    }
}

// Readings saturate at long.MaxValue when merged; totals across them must not overflow either.
internal static class SaturatingSum
{
    public static long Saturated<T>(this IEnumerable<T> items, Func<T, long> value)
    {
        long total = 0;
        foreach (var item in items)
        {
            var next = value(item);
            total = next > long.MaxValue - total ? long.MaxValue : total + next;
        }

        return total;
    }
}
