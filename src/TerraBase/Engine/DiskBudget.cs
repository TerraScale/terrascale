using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Engine;

/// <summary>Node-wide admission for bulk disk allocations, including retained import sources.</summary>
public interface IDiskBudget
{
    /// <summary>Reserves before allocating. Existing bytes are conservatively counted again until release.</summary>
    IDisposable Reserve(long bytes);

    /// <summary>Recovers a retained allocation at startup without refusing service on a full volume.</summary>
    IDisposable Recover(long bytes);
}

/// <summary>Bulk admission leaves a GiB outside estimated claims for foreground and platform metadata writes.</summary>
internal sealed class DiskBudget : IDiskBudget
{
    internal const long Headroom = 1L << 30;
    // Copies overlap their input, WAL, indexes, encrypted bundle and native sorter spill.
    internal static long WorkspaceBytes(long sourceBytes) => checked(8 * sourceBytes + (64L << 20));
    internal static long FileBytes(string path) => new[] { path, path + "-wal" }.Sum(file => File.Exists(file) ? new FileInfo(file).Length : 0);
    private readonly Lock _gate = new();
    private readonly string _root;
    private readonly Counter<long> _refused;
    private long _reserved;

    public DiskBudget(TerraBaseOptions options, IMeterFactory meters) : this(options.DataDirectory, meters.Create(Telemetry.MeterName)) { }

    internal DiskBudget(string root, Meter meter)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        _refused = meter.CreateCounter<long>("terrabase.disk.refused", description: "Bulk jobs refused before disk allocation.");
        meter.CreateObservableGauge("terrabase.disk.headroom", () => Headroom, "By", "Capacity bulk admission leaves for foreground and metadata writes.");
        meter.CreateObservableGauge("terrabase.disk.reserved", () => Interlocked.Read(ref _reserved), "By", "Outstanding bulk disk reservations.");
        meter.CreateObservableGauge("terrabase.disk.free", () => new DriveInfo(_root).AvailableFreeSpace, "By", "Available bytes on the managed data volume.");
    }

    public IDisposable Reserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            var available = Math.Max(0, new DriveInfo(_root).AvailableFreeSpace - _reserved - Headroom);
            if (bytes > available)
            {
                _refused.Add(1);
                throw new DiskCapacityException(bytes, available);
            }

            return Recover(bytes);
        }
    }

    public IDisposable Recover(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            _reserved = checked(_reserved + bytes);
            return new Reservation(this, bytes);
        }
    }

    private sealed class Reservation(DiskBudget owner, long bytes) : IDisposable
    {
        private DiskBudget? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } budget)
            {
                lock (budget._gate)
                {
                    budget._reserved -= bytes;
                }
            }
        }
    }
}

/// <summary>The node cannot safely start bulk work; callers can retry after other jobs finish or capacity is freed.</summary>
public sealed class DiskCapacityException(long required, long available)
    : Exception($"Bulk work needs {required} bytes; {available} bytes are available after reservations and protected write headroom. Retry after active jobs finish or free data-volume capacity.");
