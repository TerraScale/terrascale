using System.Diagnostics.Metrics;

namespace TerraScale.TerraBase.Features.Memory;

/// <summary>A fixed number of bytes shared by every lane.</summary>
/// <remarks>
/// ponytail: one shared limit, no per-lane quotas; add them when replication or bulk work reserves memory
/// and must not be starved by caches.
/// </remarks>
internal sealed class MemoryBudget : IMemoryBudget
{
    private readonly long _limit;
    private long _reserved;

    public MemoryBudget(long limit, Meter meter)
    {
        _limit = limit;
        meter.CreateObservableGauge("terrabase.memory.reserved", () => Volatile.Read(ref _reserved), "By", "Bytes reserved from memory-budget.");
    }

    public IMemoryReservation? TryReserve(MemoryLane lane, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        // Checked before adding, so a large request cannot overflow and a refused one never touches the counter.
        var reserved = Volatile.Read(ref _reserved);
        while (true)
        {
            if (bytes > _limit - reserved)
            {
                return null;
            }

            var seen = Interlocked.CompareExchange(ref _reserved, reserved + bytes, reserved);
            if (seen == reserved)
            {
                break;
            }

            reserved = seen;
        }

        return new Reservation(this, bytes);
    }

    private sealed class Reservation(MemoryBudget budget, long bytes) : IMemoryReservation
    {
        private int _released;

        public long Bytes => bytes;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Add(ref budget._reserved, -bytes);
            }
        }
    }
}
