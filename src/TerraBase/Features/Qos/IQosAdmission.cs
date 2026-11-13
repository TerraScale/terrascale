using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

public interface IQosAdmission
{
    IDisposable? TryEnter(TenantContext tenant, out int retryAfter);

    void ChargeEngine(TenantContext tenant, TimeSpan spent);
}
