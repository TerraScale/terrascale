using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>Organization request admission, shared across keys and projects.</summary>
public interface IQosAdmission
{
    /// <summary>
    /// Reserves one concurrent request and one request token, or returns the retry delay in seconds. Reads (GET and HEAD) and
    /// every other request hold separate lanes of the plan's limits, so readers can never starve the organization's writes.
    /// </summary>
    IQosLease? TryEnter(TenantContext tenant, out int retryAfter, bool read = false);
}

/// <summary>An admitted request's concurrent slot (released on dispose) and request token.</summary>
public interface IQosLease : IDisposable
{
    /// <summary>Gives the request token back, at most once and never above the plan's burst: the answer cost no storage read.</summary>
    void Refund();
}
