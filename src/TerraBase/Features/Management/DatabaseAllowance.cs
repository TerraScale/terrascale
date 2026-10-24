using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>The live databases each plan allows an organization (<see cref="QosPolicy.Databases"/>).</summary>
internal sealed class DatabaseAllowance
{
    /// <summary>The plans' allowances (https://terrascale.tech/pricing.json).</summary>
    public DatabaseAllowance() : this(plan => QosPolicy.For(plan).Databases) { }

    /// <summary>Other allowances, for tests.</summary>
    internal DatabaseAllowance(Func<PlanTier, long> limit)
    {
        Limit = limit;
    }

    /// <summary>The allowance of a plan; <see cref="long.MaxValue"/> is unbounded.</summary>
    public Func<PlanTier, long> Limit { get; }
}
