using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

public interface IQosAdmission
{
    IDisposable? TryEnter(TenantContext tenant, out int retryAfter);

    ValueTask<(IDisposable? Lease, int RetryAfter)> EnterAsync(TenantContext tenant, CancellationToken cancellationToken);

    void ChargeEngine(TenantContext tenant, TimeSpan spent);
}
