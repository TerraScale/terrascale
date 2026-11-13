using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed class DatabaseAllowance
{
    public DatabaseAllowance() : this(tenant => QosPolicy.For(tenant).Databases) { }

    internal DatabaseAllowance(Func<TenantContext, long> limit)
    {
        Limit = limit;
    }

    public Func<TenantContext, long> Limit { get; }
}
