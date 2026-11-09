using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

// The plans' allowances: https://terrascale.tech/pricing.json.
internal sealed class DatabaseAllowance
{
    public DatabaseAllowance() : this(plan => QosPolicy.For(plan).Databases) { }

    internal DatabaseAllowance(Func<PlanTier, long> limit)
    {
        Limit = limit;
    }

    public Func<PlanTier, long> Limit { get; }
}
