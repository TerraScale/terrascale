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
    internal const int MaxEntryMeasurementBytes = 256 * 1024;
    internal const int MaxBatchMeasurementBytes = 1024 * 1024;
    private const int MaxMeasurementKeys = 256;
    private const long MeasurementBytes = 2048;
    private const long MeasurementKeyBytes = 4096;
    private const long ParsedBytesPerJsonByte = 4;
    private readonly Lock _measurements = new();
    private readonly ConcurrentDictionary<object, Action> _active = new();
    private readonly Dictionary<UsageKey, List<IMemoryReservation>> _measurementMemory = [];

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
    private readonly Counter<long> _measurementDrops = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.usage.observations.dropped", "{observation}", "Usage observations dropped by bounded memory admission.");
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
            new(new("client_bytes", options.Region, "received"), received),
            new(new("client_bytes", options.Region, "sent"), sent));
    }

    public IDisposable TrackActive(Action report)
    {
        var key = new object();
        _active[key] = report;
        return new Tracked(_active, key);
    }

    internal void ReportActive()
    {
        foreach (var report in _active.Values)
        {
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

    internal void RecordStorage(TenantContext database, long bytes, DateTimeOffset sampledAt)
    {
        Record(database, new(new("storage_bytes", options.Region, "sample"), 1, bytes), sampledAt);
        stats.StorageSampled(database, bytes);
    }

    internal static bool ValidRegion(string region) => region is not null && TerraBase.Configuration.TerraBaseOptions.IsRegion(region);

    internal static bool ValidDimension(UsageDimension dimension) =>
        ValidRegion(dimension.Region) && dimension.Meter switch
        {
            "ops" => dimension.Kind is "read" or "put" or "patch" or "delete",
            "requests" => dimension.Kind is "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "other",
            "statements" => dimension.Kind is "postgres" or "hrana",
            "client_bytes" => dimension.Kind is "sent" or "received",
            "storage_bytes" => dimension.Kind == "sample",
            _ => false,
        };

    private void Record(TenantContext database, UsageReading reading, DateTimeOffset? at = null) => Record(database, at, reading);

    private void Record(TenantContext database, DateTimeOffset? at, params ReadOnlySpan<UsageReading> readings)
    {
        foreach (var reading in readings)
        {
            if (!ValidDimension(reading.Dimension) || reading.IsZero)
            {
                continue;
            }

            Buffer(database, at, reading);
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
        }
    }
}
