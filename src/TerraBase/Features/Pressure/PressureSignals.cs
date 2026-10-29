using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Health;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Pressure;

internal sealed partial class PressureSignals : BackgroundService, IPressureSignals
{
    private const int BucketCount = 256;
    private const int MaxTrackedTenants = 4096;
    private static readonly string[] ListenerNames = ["internal", "client", "metrics"];
    private readonly Lock _gate = new();
    private readonly long[][] _latencies = [new long[BucketCount], new long[BucketCount], new long[BucketCount]];
    private readonly long[] _totals = new long[3];
    private readonly long[] _previousTotals = new long[3];
    private readonly Dictionary<string, (long Count, PlanTier? Plan)> _tenants = [];
    private readonly TerraBaseOptions _options;
    private readonly IStorageEngine _engine;
    private readonly INodeReadiness _readiness;
    private readonly TimeProvider _time;
    private readonly ILogger<PressureSignals> _logger;
    private readonly string _processId = EntityIds.New().ToString();
    private readonly string _release = typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    private PressureBody _snapshot;
    private StorageSample? _storage;
    private long _untracked;
    private long _lastTimestamp = Stopwatch.GetTimestamp();
    private long _lastWrites;
    private TimeSpan? _lastCpu;
    private long _clientConnections;

    public PressureSignals(TerraBaseOptions options, IStorageEngine engine, TimeProvider time, IMeterFactory meters, ILogger<PressureSignals> logger, INodeReadiness readiness)
    {
        _options = options;
        _engine = engine;
        _readiness = readiness;
        _time = time;
        _logger = logger;
        _snapshot = new PressureBody
        {
            Node = options.MachineId,
            Region = options.Region,
            ProcessId = _processId,
            Release = _release,
            SampledAt = time.GetUtcNow(),
            MemoryBudgetBytes = options.MemoryBudget,
            Load = new PressureLoad(0, 0),
            Listeners = Array.AsReadOnly(ListenerNames.Select(name => new ListenerPressure(name, 0, 0, 0)).ToArray()),
            TopTenants = [],
            TopTenantsByStorage = [],
        };
        Sample();
        RegisterMetrics(meters.Create(Telemetry.MeterName));
    }

    public PressureBody Snapshot => Volatile.Read(ref _snapshot);

    public void ConnectionOpened() => Interlocked.Increment(ref _clientConnections);

    public void ConnectionClosed() => Interlocked.Decrement(ref _clientConnections);

    public void Record(TerraBaseListener listener, TimeSpan elapsed, string? organization, PlanTier? plan = null)
    {
        var index = (int)listener;
        var bucket = (int)Math.Clamp(Math.Ceiling(Math.Log(Math.Max(0.01, elapsed.TotalMilliseconds) / 0.01, 1.1)), 0, BucketCount - 1);
        lock (_gate)
        {
            _totals[index]++;
            _latencies[index][bucket]++;
            if (organization is null || !EntityIds.IsValid(organization))
            {
                return;
            }

            if (_tenants.TryGetValue(organization, out var count))
            {
                _tenants[organization] = (count.Count + 1, plan ?? count.Plan);
            }
            else if (_tenants.Count < MaxTrackedTenants)
            {
                _tenants.Add(organization, (1, plan));
            }
            else
            {
                _untracked++;
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Yield();
                Sample(await _readiness.IsReadyAsync(stoppingToken).ConfigureAwait(false));
            }
        }, stoppingToken),
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), _time);
            do
            {
                await Task.Yield();
                StoragePressure stored;
                try
                {
                    await _engine.RefreshStoragePressureAsync(stoppingToken).ConfigureAwait(false);
                    stored = _engine.StoragePressure;
                }
                catch (ObjectDisposedException)
                {
                    stored = new StoragePressure(null, []);
                }

                Volatile.Write(ref _storage, new StorageSample(stored, stored.PhysicalBytes is null ? null : _time.GetUtcNow()));
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }, stoppingToken));

    private void Sample(bool? localReady = null)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.000001, Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds);
        _lastTimestamp = timestamp;
        var listeners = new ListenerPressure[3];
        var combined = new long[BucketCount];
        TenantPressure[] tenants;
        long untracked;
        lock (_gate)
        {
            for (var index = 0; index < listeners.Length; index++)
            {
                var count = _totals[index] - _previousTotals[index];
                listeners[index] = new ListenerPressure(ListenerNames[index], count / seconds, Percentile(_latencies[index]), _totals[index]);
                _previousTotals[index] = _totals[index];
                for (var bucket = 0; bucket < BucketCount; bucket++)
                {
                    combined[bucket] += _latencies[index][bucket];
                    _latencies[index][bucket] = 0;
                }
            }

            tenants = [.. _tenants.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(10).Select(pair => new TenantPressure(pair.Key, pair.Value.Count / seconds, pair.Value.Plan switch
                {
                    PlanTier.Hobby => "hobby",
                    PlanTier.Starter => "starter",
                    PlanTier.Pro => "pro",
                    PlanTier.Team => "team",
                    PlanTier.Enterprise => "enterprise",
                    _ => null,
                }))];
            _tenants.Clear();
            untracked = _untracked;
            _untracked = 0;
        }

        long? memory = null;
        double? cpu = null;
        try
        {
            var workingSet = Environment.WorkingSet;
            memory = workingSet > 0 ? workingSet : null;
            var totalCpu = Environment.CpuUsage.TotalTime;
            if (_lastCpu is { } previous)
            {
                cpu = Math.Clamp((totalCpu - previous).TotalSeconds / seconds / Environment.ProcessorCount, 0, 1);
            }

            _lastCpu = totalCpu;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _lastCpu = null;
            _logger.ZLogWarning(error, $"Pressure measurement {"process":@Signal} is unavailable");
        }

        long? diskTotal = null;
        long? diskFree = null;
        try
        {
            var drive = new DriveInfo(_options.DataDirectory);
            diskTotal = drive.TotalSize;
            diskFree = drive.TotalFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.ZLogWarning(error, $"Pressure measurement {"disk":@Signal} is unavailable");
        }

        var writes = _engine.CommittedWrites;
        var writeRate = Math.Max(0, writes - _lastWrites) / seconds;
        _lastWrites = writes;
        var storage = Volatile.Read(ref _storage);
        Volatile.Write(ref _snapshot, new PressureBody
        {
            Node = _options.MachineId,
            LocalReady = localReady,
            IoAdmittedCount = _engine.IoAdmittedCount,
            Region = _options.Region,
            ProcessId = _processId,
            Release = _release,
            SampledAt = _time.GetUtcNow(),
            SampleWindowSeconds = seconds,
            CpuUtil = cpu,
            MemoryUtil = memory is { } used ? Math.Clamp((double)used / _options.MemoryBudget, 0, 1) : null,
            MemoryUsedBytes = memory,
            MemoryBudgetBytes = _options.MemoryBudget,
            DiskTotalBytes = diskTotal,
            DiskFreeBytes = diskFree,
            DiskUsedBytes = diskTotal - diskFree,
            RequestRate = listeners.Sum(listener => listener.RequestRate),
            P95LatencyMs = Percentile(combined),
            Load = new PressureLoad(writeRate, Interlocked.Read(ref _clientConnections)),
            Listeners = Array.AsReadOnly(listeners),
            TopTenants = Array.AsReadOnly(tenants),
            UntrackedTenantRequests = untracked,
            DataBytes = storage?.Pressure.PhysicalBytes,
            StorageSampledAt = storage?.SampledAt,
            TopTenantsByStorage = Array.AsReadOnly(storage?.Pressure.TopTenants.Select(tenant => new TenantStoragePressure(tenant.Org, tenant.Bytes)).ToArray() ?? []),
        });
    }
    private static double Percentile(long[] buckets)
    {
        var count = buckets.Sum();
        if (count == 0)
        {
            return 0;
        }

        var target = (long)Math.Ceiling(count * 0.95);
        long accumulated = 0;
        for (var index = 0; index < buckets.Length; index++)
        {
            accumulated += buckets[index];
            if (accumulated >= target)
            {
                return Math.Min(1e9, 0.01 * Math.Pow(1.1, index));
            }
        }

        return 0;
    }

    private sealed record StorageSample(StoragePressure Pressure, DateTimeOffset? SampledAt);
}
