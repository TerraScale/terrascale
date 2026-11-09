namespace TerraBase.Features.Qos;

public sealed record QosRequestLease(IDisposable Lease)
{
    public void Release() => Lease.Dispose();
}
