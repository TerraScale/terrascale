using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>Organization request admission, shared across keys and projects.</summary>
public interface IQosAdmission
{
    /// <summary>Reserves one concurrent request, or returns the retry delay in seconds.</summary>
    IDisposable? TryEnter(TenantContext tenant, out int retryAfter);
}
