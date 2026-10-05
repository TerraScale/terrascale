namespace TerraScale.TerraBase.Features.Memory;

/// <summary>Who a reservation is charged to. Bounded set; never a tenant-controlled value as a metric label.</summary>
public enum MemoryLane
{
    /// <summary>Client reads and writes.</summary>
    Foreground,

    /// <summary>Replication traffic.</summary>
    Replication,

    /// <summary>Backup, restore, repair and other bulk work.</summary>
    Bulk,
}

/// <summary>Reserved bytes; disposing returns them to the budget. Safe to dispose more than once.</summary>
public interface IMemoryReservation : IDisposable
{
    /// <summary>Reserved bytes.</summary>
    long Bytes { get; }
}

/// <summary>
/// Process memory admission. Reserve before reading, decoding, queueing or materializing data,
/// and release on completion, cancellation and error. Sized by <c>memory-budget</c> (<c>docs/memory.md</c>).
/// </summary>
public interface IMemoryBudget
{
    /// <summary>Reserves <paramref name="bytes"/> or returns <see langword="null"/> when the lane is exhausted.</summary>
    IMemoryReservation? TryReserve(MemoryLane lane, long bytes);
}
