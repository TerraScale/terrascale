using TerraBase.Tenancy;

namespace TerraBase.Features.Memory;
public enum MemoryLane
{
    Foreground,
    Bulk,
}
public interface IMemoryReservation : IDisposable
{
    long Bytes { get; }
}
public interface IMemoryBudget
{
    IMemoryReservation? TryReserve(MemoryLane lane, long bytes);
    IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant);
    bool TryGrow(IMemoryReservation reservation, long bytes) => false;
}
