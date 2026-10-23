using System.Diagnostics;
using System.Globalization;
using TerraBase.Errors;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Accepted work, an exhausted monthly allowance or unavailable accounting.</summary>
public readonly union OperationAllowanceCheck(Done, QuotaExceeded, Unavailable);

/// <summary>
/// The hard monthly operation allowance of an organization's plan (https://terrascale.tech/pricing.json): once its
/// operation units this UTC month, summed over every project, region and node, reach it, new puts and patches stop;
/// reads and deletes remain available.
/// </summary>
public interface IOperationAllowance
{
    /// <summary>Whether <paramref name="database"/>'s organization may start new billable writes.</summary>
    ValueTask<OperationAllowanceCheck> CheckAsync(TenantContext database, CancellationToken cancellationToken);
}

internal sealed class OperationAllowance : IOperationAllowance
{
    private readonly UsageMeter _meter;
    private readonly ITenantDirectory _directory;
    private readonly Func<PlanTier, long> _limit;

    public OperationAllowance(UsageMeter meter, ITenantDirectory directory) : this(meter, directory, plan => QosPolicy.For(plan).MonthlyOperations) { }

    // Integration fixtures can exercise the exact allowance boundary without changing the production plan policy.
    internal OperationAllowance(UsageMeter meter, ITenantDirectory directory, Func<PlanTier, long> limit)
    {
        _meter = meter;
        _directory = directory;
        _limit = limit;
    }

    public async ValueTask<OperationAllowanceCheck> CheckAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var current = await _directory.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (current is not PlanTier plan)
        {
            return current switch
            {
                Unavailable error => error,
                PlanTier => throw new UnreachableException(),
            };
        }

        var limit = _limit(plan);
        if (limit == long.MaxValue)
        {
            return new Done();
        }

        var counted = await _meter.MonthUnitsAsync(database, cancellationToken).ConfigureAwait(false);
        if (counted is not long units)
        {
            return counted switch
            {
                Unavailable error => error,
                long => throw new UnreachableException(),
            };
        }

        return units < limit
            ? new Done()
            : new QuotaExceeded(string.Create(CultureInfo.InvariantCulture,
                $"the organization used its {plan.ToString().ToLowerInvariant()} plan allowance of {limit:N0} operation units this month; upgrade the plan at https://dash.terrascale.tech/organization?tab=billing to continue writing. Reads and deletes remain available"),
                "operation_allowance");
    }
}
