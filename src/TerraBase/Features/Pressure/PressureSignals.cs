using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Pressure;

public sealed record PressureBody(
    string Region,
    string Machine,
    long Connections,
    long InFlight,
    long Requests,
    double CpuUtilization,
    long MemoryUsedBytes,
    long MemoryBudgetBytes,
    long DiskFreeBytes,
    long DiskTotalBytes,
    DateTimeOffset SampledAt);

internal sealed class PressureSignals
{
    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private long _connections;
    private long _inFlight;
    private long _requests;
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private long _lastSample = Stopwatch.GetTimestamp();

    public PressureSignals(TerraBaseOptions options, TimeProvider time, IMeterFactory meters)
    {
        _options = options;
        _time = time;
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase_pressure_connections", () => Interlocked.Read(ref _connections));
        meter.CreateObservableGauge("terrabase_pressure_in_flight", () => Interlocked.Read(ref _inFlight));
        meter.CreateObservableCounter("terrabase_pressure_requests", () => Interlocked.Read(ref _requests));
        meter.CreateObservableGauge("terrabase_pressure_memory_used_bytes", static () => GC.GetTotalMemory(forceFullCollection: false));
        meter.CreateObservableGauge("terrabase_pressure_memory_budget_bytes", () => _options.MemoryBudget);
    }

    public void ConnectionOpened() => Interlocked.Increment(ref _connections);

    public void ConnectionClosed() => Interlocked.Decrement(ref _connections);

    public IDisposable Request()
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _inFlight);
        return new Lease(this);
    }

    public PressureBody Sample()
    {
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Exchange(ref _lastSample, now), now);
        var used = cpu - _lastCpu;
        _lastCpu = cpu;
        var utilization = elapsed > TimeSpan.Zero ? Math.Clamp(used / elapsed / Environment.ProcessorCount, 0, 1) : 0;
        var (free, total) = Disk(_options.DataDirectory);
        return new PressureBody(_options.Region, _options.MachineId, Interlocked.Read(ref _connections), Interlocked.Read(ref _inFlight), Interlocked.Read(ref _requests),
            utilization, process.WorkingSet64, _options.MemoryBudget, free, total, _time.GetUtcNow());
    }

    private static (long Free, long Total) Disk(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? "/");
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }

    private sealed class Lease(PressureSignals signals) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref signals._inFlight);
            }
        }
    }
}
