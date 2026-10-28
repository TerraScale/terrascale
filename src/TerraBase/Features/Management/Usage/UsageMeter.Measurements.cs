using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal sealed partial class UsageMeter
{
    // Keep total-budget gauges and usage admission in the same observation while recorders or flushes run.
    internal void ObserveMemory(Action observe)
    {
        lock (_measurements)
        {
            observe();
        }
    }

    internal long ReservedMeasurementBytes
    {
        get
        {
            lock (_measurements)
            {
                return _measurementMemory.Values.Sum(reservations => reservations.Sum(reservation => reservation.Bytes));
            }
        }
    }

    internal const int MaxDimensions = 256;
    // What a follower's usage batch carries as measurement JSON, per entry and in total; the primary refuses more.
    internal const int MaxEntryMeasurementBytes = 256 * 1024;
    internal const int MaxBatchMeasurementBytes = 1024 * 1024;
    private const int MaxMeasurementKeys = 256;
    // Owner/dimension strings, dictionaries, histogram arrays and overlapping immutable snapshots.
    private const long MeasurementBytes = 2048;
    private const long MeasurementKeyBytes = 4096;
    // Parsed readings, strings, dictionaries and histograms per byte of measurement JSON.
    private const long ParsedBytesPerJsonByte = 4;
    private readonly Lock _measurements = new();
    // Requests in progress that report their transferred bytes on each flush tick; bounded by the server's open requests.
    private readonly ConcurrentDictionary<object, Action> _active = new();
    private readonly Dictionary<UsageKey, List<IMemoryReservation>> _measurementMemory = [];

    /// <summary>The memory pending measurements hold until a flush writes them.</summary>
    internal long MeasurementBytesHeld
    {
        get
        {
            lock (_measurements)
            {
                return _measurementMemory.Values.Sum(reservations => reservations.Sum(reservation => reservation.Bytes));
            }
        }
    }
    // Open backlogs by database and peer region; when full, the least recently observed one (a removed database or peer) goes.
    private readonly Dictionary<(DatabaseOwner Database, string Region), (DateTimeOffset Started, DateTimeOffset? Applied, DateTimeOffset Seen)> _backlogs = [];
    private readonly Counter<long> _measurementDrops = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.usage.observations.dropped", "{observation}", "Usage observations dropped by bounded memory admission.");
    private readonly Counter<long> _replicationBytes = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "replication_bytes", "By", "Encoded replication message bytes, including repeated attempts and protocol control messages.");

    public void RecordOperation(TenantContext database, string kind, int count)
    {
        RequestLogOperations.Current.Value?.Add(database, count);
        if (count != 0 && Dimension("ops", kind) is { } dimension)
        {
            Buffer(database, null, dimension, count, 0, latencyIndex: -1);
        }
    }

    public void RecordRequest(TenantContext database, string method, int status, TimeSpan elapsed, long received, long sent)
    {
        var kind = method switch
        {
            "GET" => "get",
            "POST" => "post",
            "PUT" => "put",
            "PATCH" => "patch",
            "DELETE" => "delete",
            "HEAD" => "head",
            "OPTIONS" => "options",
            _ => "other",
        };
        BufferRequest(database, Dimension("requests", kind), status >= 400 ? 1 : 0, LatencyIndex(elapsed),
            Dimension("client_bytes", "received"), received, Dimension("client_bytes", "sent"), sent);
        stats.Request(database, elapsed, status >= 400);
    }

    public void RecordStatement(TenantContext database, string protocol, bool failed, TimeSpan elapsed)
    {
        RequestLogOperations.Current.Value?.Statement(database, failed);
        if (Dimension("statements", protocol) is { } dimension)
        {
            Buffer(database, null, dimension, 1, failed ? 1 : 0, LatencyIndex(elapsed));
        }
    }

    private static int LatencyIndex(TimeSpan elapsed)
    {
        var index = 0;
        while (index < UsageReading.LatencyBounds.Count - 1 && elapsed.TotalMilliseconds > UsageReading.LatencyBounds[index])
        {
            index++;
        }

        return index;
    }

    public void RecordClientBytes(TenantContext database, long received, long sent, DateTimeOffset? transferredAt = null)
    {
        Record(database, transferredAt,
            new(new("client_bytes", options.Region, "", "received"), received),
            new(new("client_bytes", options.Region, "", "sent"), sent));
    }

    public IDisposable TrackActive(Action report)
    {
        var key = new object();
        _active[key] = report;
        return new Tracked(_active, key);
    }

    // Each flush tick first reports what requests still in progress transferred.
    internal void ReportActive()
    {
        foreach (var report in _active.Values)
        {
            // One request's failure must not stop the others' reports or the flushes after them.
            try
            {
                report();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning(error, $"Reporting a request's transferred bytes failed; its other requests and the flush go on");
            }
        }
    }

    private sealed class Tracked(ConcurrentDictionary<object, Action> active, object key) : IDisposable
    {
        public void Dispose() => active.TryRemove(key, out _);
    }

    public void RecordReplicationBytes(TenantContext database, string peerRegion, string kind, bool sent, long bytes)
    {
        if (bytes <= 0 || !ValidRegion(peerRegion) || kind is not ("delta" or "anti_entropy" or "handoff"))
        {
            return;
        }

        var direction = sent ? "sent" : "received";
        _replicationBytes.Add(bytes, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("direction", direction));
        Record(database, new(new("replication_bytes", options.Region, peerRegion, kind + ":" + direction), bytes));
        stats.ReplicationBytes(database, peerRegion, sent, bytes);
    }

    public void RecordReplicatedOperations(TenantContext database, string peerRegion, int operations)
    {
        if (!ValidRegion(peerRegion) || operations <= 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        lock (_measurements)
        {
            var key = (DatabaseOwner.Of(database), peerRegion);
            if (_backlogs.TryGetValue(key, out var state))
            {
                _backlogs[key] = (state.Started, now, state.Seen);
            }
        }

        Record(database, new(new("replicated_ops", options.Region, peerRegion, "applied"), operations, State: new(now, 0, false, null, now)));
    }

    public void RecordReplicationState(TenantContext database, string peerRegion, long remaining, bool diverged, double? convergenceMs = null)
    {
        if (!ValidRegion(peerRegion))
        {
            return;
        }

        var now = time.GetUtcNow();
        UsageReplicationState observation;
        lock (_measurements)
        {
            var key = (DatabaseOwner.Of(database), peerRegion);
            if (!_backlogs.TryGetValue(key, out var backlog))
            {
                if (remaining > 0 && _backlogs.Count >= MaxMeasurementKeys)
                {
                    _measurementDrops.Add(1);
                    _backlogs.Remove(_backlogs.MinBy(entry => entry.Value.Seen).Key);
                }

                backlog = (now, null, now);
            }

            var lag = remaining > 0 ? Math.Max(0, (now - backlog.Started).TotalMilliseconds) : 0;
            var convergence = convergenceMs ?? (remaining == 0 && backlog.Started < now ? (now - backlog.Started).TotalMilliseconds : null);
            observation = new(now, lag, diverged, convergence, backlog.Applied, remaining);
            if (remaining > 0)
            {
                _backlogs[key] = backlog with { Seen = now };
            }
            else
            {
                _backlogs.Remove(key);
            }
        }

        Record(database, new(new("replication_status", options.Region, peerRegion, "inbound"), 1, State: observation));
        stats.ReplicationState(database, peerRegion, remaining, observation.LagMs, diverged);
    }

    internal void RecordPeerState(TenantContext database, string peerRegion, long remaining, double lagMs, bool diverged, double? convergenceMs)
    {
        if (ValidRegion(peerRegion))
        {
            Record(database, new(new("replication_status", options.Region, peerRegion, "outbound"), 1,
                State: new(time.GetUtcNow(), lagMs, diverged, convergenceMs, null, remaining)));
            stats.ReplicationState(database, peerRegion, remaining, lagMs, diverged);
        }
    }

    internal void RecordStorage(TenantContext database, long bytes, DateTimeOffset sampledAt)
    {
        Record(database, new(new("storage_bytes", options.Region, "", "sample"), 1, bytes), sampledAt);
        stats.StorageSampled(database, bytes);
    }

    internal static bool ValidRegion(string region) => region is not null && TerraBase.Configuration.TerraBaseOptions.IsRegion(region);

    internal static bool ValidDimension(UsageDimension dimension) =>
        ValidRegion(dimension.Region) && dimension.Meter switch
        {
            "ops" => dimension.PeerRegion == "" && dimension.Kind is "read" or "put" or "patch" or "delete",
            "requests" => dimension.PeerRegion == "" && dimension.Kind is "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "other",
            "statements" => dimension.PeerRegion == "" && dimension.Kind is "postgres" or "hrana",
            "client_bytes" => dimension.PeerRegion == "" && dimension.Kind is "sent" or "received",
            "storage_bytes" => dimension.PeerRegion == "" && dimension.Kind == "sample",
            "replication_bytes" => ValidRegion(dimension.PeerRegion) && dimension.Kind is "delta:sent" or "delta:received"
                or "anti_entropy:sent" or "anti_entropy:received" or "handoff:sent" or "handoff:received",
            "replicated_ops" => ValidRegion(dimension.PeerRegion) && dimension.Kind == "applied",
            "replication_status" => ValidRegion(dimension.PeerRegion) && dimension.Kind is "inbound" or "outbound",
            _ => false,
        };

    private void Record(TenantContext database, UsageReading reading, DateTimeOffset? at = null) => Record(database, at, reading);

    // Valid, non-zero readings of one minute: additive ones buffered, those with replication state straight to the pending counts.
    private void Record(TenantContext database, DateTimeOffset? at, params ReadOnlySpan<UsageReading> readings)
    {
        foreach (var reading in readings)
        {
            if (!ValidDimension(reading.Dimension) || reading.IsZero)
            {
                continue;
            }

            if (reading.State is null)
            {
                Buffer(database, at, reading);
                continue;
            }

            var minute = (at ?? time.GetUtcNow()).ToUnixTimeSeconds() / 60;
            var key = new UsageKey(DatabaseOwner.Of(database), minute / 60, UsageScope.Minute, (int)(minute % 60));
            Add(key, new(0, 0, new Dictionary<string, UsageReading>(StringComparer.Ordinal) { [reading.Dimension.Key] = reading }));
        }
    }

    private void AddMeasurements(UsageKey key, OpCounts counts)
    {
        lock (_measurements)
        {
            var current = _pending.TryGetValue(key.Database, out var pending) ? pending.GetValueOrDefault(key) : null;
            var added = counts.Meters!.Keys.Count(dimension => current?.Meters?.ContainsKey(dimension) != true);
            var dimensions = (current?.Meters?.Count ?? 0) + added;
            var existing = _measurementMemory.TryGetValue(key, out var reservations);
            var needed = Math.Max(0, MeasurementKeyBytes + dimensions * MeasurementBytes - (reservations?.Sum(reservation => reservation.Bytes) ?? 0));
            if (dimensions > MaxDimensions || !existing && _measurementMemory.Count >= MaxMeasurementKeys)
            {
                _measurementDrops.Add(1);
                AddCore(key, counts with { Meters = null });
                return;
            }

            if (needed > 0)
            {
                if (memory.TryReserve(MemoryLane.Foreground, needed, tenant: null) is not { } reservation)
                {
                    _measurementDrops.Add(1);
                    AddCore(key, counts with { Meters = null });
                    return;
                }

                if (reservations is null)
                {
                    reservations = [];
                    _measurementMemory.Add(key, reservations);
                }

                reservations.Add(reservation);
            }

            AddCore(key, counts);
        }
    }

    private void FoldMeasurements(UsageKey key, UsageKey folded, OpCounts counts)
    {
        lock (_measurements)
        {
            if (!_pending.TryGetValue(key.Database, out var pending) || !pending.TryRemove(KeyValuePair.Create(key, counts)))
            {
                return;
            }

            // Transfer the existing admission; compaction must not drop observations by admitting them twice.
            if (_measurementMemory.Remove(key, out var reservations))
            {
                if (_measurementMemory.TryGetValue(folded, out var destination))
                {
                    destination.AddRange(reservations);
                }
                else
                {
                    _measurementMemory.Add(folded, reservations);
                }
            }

            AddCore(folded, counts);
            if (_measurementMemory.TryGetValue(folded, out var retained))
            {
                var needed = MeasurementKeyBytes + (pending[folded].Meters?.Count ?? 0) * MeasurementBytes;
                var reserved = retained.Sum(reservation => reservation.Bytes);
                for (var i = retained.Count - 1; i >= 0; i--)
                {
                    if (reserved - retained[i].Bytes >= needed)
                    {
                        reserved -= retained[i].Bytes;
                        retained[i].Dispose();
                        retained.RemoveAt(i);
                    }
                }
            }
        }
    }

    private void ReleaseMeasurement(UsageKey key, OpCounts remaining)
    {
        if (remaining != OpCounts.Zero)
        {
            return;
        }

        lock (_measurements)
        {
            // A recorder may have added to the same key between subtraction and this lock.
            if ((!_pending.TryGetValue(key.Database, out var pending) || !pending.ContainsKey(key)) && _measurementMemory.Remove(key, out var reservations))
            {
                foreach (var reservation in reservations)
                {
                    reservation.Dispose();
                }
            }
        }
    }

    private void DisposeMeasurements()
    {
        lock (_measurements)
        {
            foreach (var reservation in _measurementMemory.Values.SelectMany(reservations => reservations))
            {
                reservation.Dispose();
            }

            _measurementMemory.Clear();
            _backlogs.Clear();
        }
    }
}
