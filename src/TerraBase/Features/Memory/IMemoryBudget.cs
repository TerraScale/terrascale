using TerraBase.Tenancy;

namespace TerraBase.Features.Memory;

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
    /// <summary>
    /// Reserves bytes for the ambient QoS organization, or only the process for system work.
    /// Transient operations and retained cache entries share the same admission limit; this is not a usage quota.
    /// Returns null when either limit or the bounded reservation table is exhausted.
    /// </summary>
    IMemoryReservation? TryReserve(MemoryLane lane, long bytes);

    /// <summary>
    /// Reserves bytes against both the process limit and the explicit organization's plan share.
    /// A null tenant charges only the process, independently of the ambient request.
    /// </summary>
    IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant);

    /// <summary>
    /// Adds bytes to a live reservation this budget issued, against the same process and organization limits but without
    /// another reservation table entry. False when refused or unsupported; the caller then reserves anew.
    /// </summary>
    bool TryGrow(IMemoryReservation reservation, long bytes) => false;
}
