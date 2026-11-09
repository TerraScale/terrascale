using TerraBase.Tenancy;

namespace TerraBase.Features.Memory;

public interface IMemoryReservation : IDisposable
{
    long Bytes { get; }
}
public interface IMemoryBudget
{
    IMemoryReservation? TryReserve(long bytes);
    IMemoryReservation? TryReserve(long bytes, TenantContext? tenant);
    bool TryGrow(IMemoryReservation reservation, long bytes) => false;
    void Shrink(IMemoryReservation reservation, long bytes)
    {
    }
}
