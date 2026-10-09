using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Management.Usage;

internal sealed class UsageAnalytics(IManagementService management, UsageMeter meter, TerraBaseOptions options, TimeProvider time, IMemoryBudget memory)
    : IUsageAnalytics
{
    private const int MaxPoints = 16384;
    // Health sources refresh every ten seconds; a peer silent past Fresh is stopped, and past Forget it was removed.
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Forget = TimeSpan.FromMinutes(10);

    public async ValueTask<ProjectAnalytics?> AnalyticsAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var databases = await DatabasesAsync(access, database, started, cancellationToken).ConfigureAwait(false);
        if (databases is null)
        {
            return null;
        }

        var (reservation, reserved) = Reserve(databases, window, region);
        using var loaded = new Loaded(memory, reserved, reservation);
        var items = new List<DatabaseAnalytics>();
        foreach (var record in databases)
        {
            CheckDeadline(started);
            if (await management.GetDatabaseTenantAsync(access.Actor, access.OrganizationId, access.ProjectId, record.Id, cancellationToken)
                .ConfigureAwait(false) is not { } tenant)
            {
                continue;
            }

            var (lifetime, series) = await meter.GetObservationsAsync(tenant, window.Usage, cancellationToken, () => CheckDeadline(started), loaded.Filter(region))
                .ConfigureAwait(false);
            foreach (var observing in Regions(record, lifetime, region, window, loaded))
            {
                items.Add(new(record.Id, record.Name, observing, [.. series.Select((point, index) => Point(
                    window.Usage.Granularity.Start(window.Usage.First + index), Readings(point, observing)))]));
            }
        }

        CheckDeadline(started);
        return loaded.HandOff(retain, new ProjectAnalytics(access.ProjectId, window.Range, window.Usage.From, window.Usage.To, window.Usage.Granularity.Minutes * 60, items));
    }

    public async ValueTask<ProjectReplication?> ReplicationAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var databases = await DatabasesAsync(access, database, started, cancellationToken).ConfigureAwait(false);
        if (databases is null)
        {
            return null;
        }

        var (reservation, reserved) = Reserve(databases, window, region);
        using var loaded = new Loaded(memory, reserved, reservation);
        var items = new List<DatabaseReplication>();
        foreach (var record in databases)
        {
            CheckDeadline(started);
            if (await management.GetDatabaseTenantAsync(access.Actor, access.OrganizationId, access.ProjectId, record.Id, cancellationToken)
                .ConfigureAwait(false) is not { } tenant)
            {
                continue;
            }

            var (lifetime, series) = await meter.GetObservationsAsync(tenant, window.Usage, cancellationToken, () => CheckDeadline(started), loaded.Filter(region))
                .ConfigureAwait(false);
            foreach (var observing in Regions(record, lifetime, region, window, loaded))
            {
                var readings = Readings(lifetime, observing).ToArray();
                // Every peer observed within Forget counts; one fresh healthy peer cannot hide a silent or diverged one.
                var now = time.GetUtcNow();
                var health = readings.Where(reading => reading.Dimension.Meter == "replication_status" && reading.State is not null)
                    .Select(reading => reading.State!).Where(state => now - state.ObservedAt <= Forget).ToArray();
                var recent = health.Where(state => now - state.ObservedAt <= Fresh).ToArray();
                var applied = readings.Where(reading => reading.Dimension.Meter == "replicated_ops").Select(reading => reading.State?.LastApplied).Max();
                var lag = recent.Length == 0 ? (double?)null : recent.Max(state => state.LagMs);
                // Without a fresh observation nothing is current: stopped, with neither lag nor divergence.
                var diverged = recent.Length == 0 ? (bool?)null : health.Any(state => state.Diverged);
                var status = diverged == true ? "diverged" : recent.Length < health.Length || recent.Length == 0 ? "stopped"
                    : recent.Any(state => state.Remaining > 0) ? "lagging" : "healthy";
                var points = series.Select((point, index) =>
                {
                    var values = Readings(point, observing).ToArray();
                    return new ReplicationPoint(window.Usage.Granularity.Start(window.Usage.First + index),
                        values.Where(reading => reading.Dimension.Meter == "replicated_ops").Sum(reading => reading.Count), Bandwidth(values));
                }).ToArray();
                items.Add(new(record.Id, record.Name, observing, status, points.Sum(point => point.ReplicatedOps), lag, diverged,
                    health.Select(state => state.ConvergenceMs).Max(), applied, Bandwidth(series.SelectMany(point => Readings(point, observing))), points));
            }
        }

        CheckDeadline(started);
        return loaded.HandOff(retain, new ProjectReplication(access.ProjectId, window.Range, window.Usage.From, window.Usage.To, window.Usage.Granularity.Minutes * 60, items));
    }

    private async ValueTask<List<ManagedDatabase>?> DatabasesAsync(ProjectAccess access, string? database, long started, CancellationToken cancellationToken)
    {
        if (database is not null)
        {
            if (access.DatabaseName is not null && database != access.DatabaseId)
            {
                return null;
            }

            return await management.GetDatabaseAsync(access.Actor, access.OrganizationId, access.ProjectId, database, cancellationToken).ConfigureAwait(false)
                is { } found ? [found] : null;
        }

        List<ManagedDatabase> result = [];
        string? after = null;
        do
        {
            CheckDeadline(started);
            var page = await management.ListDatabasesAsync(access.Actor, access.OrganizationId, access.ProjectId, after, 100, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return null;
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
            // The route answers this like its own deadline: a transient 503, not an invalid query.
            throw new OperationCanceledException("analytics exceeded its execution budget");
        }
    }

    private static IEnumerable<UsageReading> Readings(OpCounts counts, string region) =>
        counts.Meters?.Values.Where(reading => reading.Dimension.Region == region) ?? [];

    private static AnalyticsPoint Point(DateTimeOffset start, IEnumerable<UsageReading> readings)
    {
        var values = readings.ToArray();
        var requests = values.Where(reading => reading.Dimension.Meter == "requests").ToArray();
        var histogram = new long[UsageReading.LatencyBounds.Count];
        foreach (var request in requests)
        {
            for (var i = 0; i < histogram.Length; i++)
            {
                histogram[i] += request.Latency?[i] ?? 0;
            }
        }

        var storage = values.Where(reading => reading.Dimension.Meter == "storage_bytes").ToArray();
        var samples = storage.Sum(reading => reading.Count);
        return new(start, ByKind(requests), ByKind(values.Where(reading => reading.Dimension.Meter == "ops")), requests.Sum(reading => reading.Value),
            new(Percentile(histogram, 0.5), Percentile(histogram, 0.95), Percentile(histogram, 0.99)),
            samples == 0 ? null : storage.Sum(reading => reading.Value) / samples,
            Traffic(values, "client_bytes"), Traffic(values, "replication_bytes"));
    }

    private static Dictionary<string, long> ByKind(IEnumerable<UsageReading> readings) =>
        readings.GroupBy(reading => reading.Dimension.Kind).ToDictionary(group => group.Key, group => group.Sum(reading => reading.Count), StringComparer.Ordinal);

    private static BandwidthUsage Traffic(IEnumerable<UsageReading> readings, string meter)
    {
        var values = readings.Where(reading => reading.Dimension.Meter == meter).ToArray();
        return new(values.Where(reading => reading.Dimension.Kind.EndsWith("sent", StringComparison.Ordinal)).Sum(reading => reading.Count),
            values.Where(reading => reading.Dimension.Kind.EndsWith("received", StringComparison.Ordinal)).Sum(reading => reading.Count));
    }

    private static ReplicationBandwidth[] Bandwidth(IEnumerable<UsageReading> readings) =>
        [.. readings.Where(reading => reading.Dimension.Meter == "replication_bytes").GroupBy(reading =>
        {
            var dimension = reading.Dimension;
            var sent = dimension.Kind.EndsWith(":sent", StringComparison.Ordinal);
            return (Source: sent ? dimension.Region : dimension.PeerRegion, Destination: sent ? dimension.PeerRegion : dimension.Region,
                Kind: dimension.Kind.Split(':')[0]);
        }).Select(group => new ReplicationBandwidth(group.Key.Source, group.Key.Destination, group.Key.Kind,
            group.Where(reading => reading.Dimension.Kind.EndsWith(":sent", StringComparison.Ordinal)).Sum(reading => reading.Count),
            group.Where(reading => reading.Dimension.Kind.EndsWith(":received", StringComparison.Ordinal)).Sum(reading => reading.Count)))
        .OrderBy(value => value.SourceRegion, StringComparer.Ordinal).ThenBy(value => value.DestinationRegion, StringComparer.Ordinal)
        .ThenBy(value => value.Kind, StringComparer.Ordinal)];

    private static double? Percentile(long[] histogram, double fraction)
    {
        var count = histogram.Sum();
        if (count == 0)
        {
            return null;
        }

        var target = (long)Math.Ceiling(count * fraction);
        long seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= target)
            {
                return UsageReading.LatencyBounds[i];
            }
        }

        return UsageReading.LatencyBounds[^1];
    }
}
