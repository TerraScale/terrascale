using System.Diagnostics;
using System.Globalization;
using TerraBase.Errors;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

public readonly union OperationAllowanceCheck(Done, QuotaExceeded, Unavailable);

public enum MeteredOperation
{
    Write,

    SqlRead,
}

public interface IOperationAllowance
{
    ValueTask<OperationAllowanceCheck> CheckAsync(TenantContext database, MeteredOperation operation, CancellationToken cancellationToken);
}

internal sealed class OperationAllowance : IOperationAllowance
{
    private readonly UsageMeter _meter;
    private readonly ITenantDirectory _directory;
    private readonly Func<PlanTier, MeteredOperation, long> _limit;

    public OperationAllowance(UsageMeter meter, ITenantDirectory directory) : this(meter, directory, Limit) { }

    internal OperationAllowance(UsageMeter meter, ITenantDirectory directory, Func<PlanTier, MeteredOperation, long> limit)
    {
        _meter = meter;
        _directory = directory;
        _limit = limit;
    }

    private static long Limit(PlanTier plan, MeteredOperation operation) => operation switch
    {
        MeteredOperation.Write => QosPolicy.For(plan).MonthlyWriteCap,
        _ => QosPolicy.For(plan).MonthlySqlReads,
    };

    public async ValueTask<OperationAllowanceCheck> CheckAsync(TenantContext database, MeteredOperation operation, CancellationToken cancellationToken)
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

        var limit = _limit(plan, operation);
        if (limit == long.MaxValue)
        {
            return new Done();
        }

        var counted = await _meter.MonthUnitsAsync(database, cancellationToken).ConfigureAwait(false);
        if (counted is not MonthUnits units)
        {
            return counted switch
            {
                Unavailable error => error,
                MonthUnits => throw new UnreachableException(),
            };
        }

        var used = operation == MeteredOperation.Write ? units.Writes : units.Reads;
        if (used < limit)
        {
            return new Done();
        }

        var name = plan.JsonName;
        return operation == MeteredOperation.Write
            ? new QuotaExceeded(string.Create(CultureInfo.InvariantCulture,
                $"the organization reached its {name} plan cap of {limit:N0} write units this month; upgrade the plan at https://dash.terrascale.tech/organization?tab=billing to continue writing. Reads remain available"),
                "operation_allowance")
            : new QuotaExceeded(string.Create(CultureInfo.InvariantCulture,
                $"the organization used its {name} plan fair use of {limit:N0} SQL read units this month; shape reads remain available and free. Upgrade the plan at https://dash.terrascale.tech/organization?tab=billing for more"),
                "read_fair_use");
    }
}
