namespace TerraBase.Indexing.Automatic;

/// <summary>Budget reservation and bounded queue accounting for automatic indexes.</summary>
public sealed class InMemoryAutomaticIndexBudgetLedger : IAutomaticIndexBudgetLedger
{
    private static readonly AutomaticIndexBudget Unlimited = new(1_000_000_000_000, ulong.MaxValue, 1_000_000_000_000, int.MaxValue);
    private readonly object sync = new();
    private readonly Dictionary<string, AutomaticIndexBudget> tenantBudgets = new(StringComparer.Ordinal);
    private readonly Dictionary<AutomaticIndexScope, AutomaticIndexBudget> collectionBudgets = [];
    private readonly Dictionary<string, MutableUsage> tenantUsage = new(StringComparer.Ordinal);
    private readonly Dictionary<AutomaticIndexScope, MutableUsage> collectionUsage = [];

    /// <summary>Sets the aggregate budget for one tenant.</summary>
    public void SetTenantBudget(string tenant, AutomaticIndexBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(budget);
        lock (sync)
        {
            tenantBudgets[tenant.Trim()] = budget;
        }
    }

    /// <summary>Sets the budget for one tenant/database/collection scope.</summary>
    public void SetCollectionBudget(AutomaticIndexScope scope, AutomaticIndexBudget budget)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(budget);
        lock (sync)
        {
            collectionBudgets[scope] = budget;
        }
    }

    /// <inheritdoc />
    public BudgetAdmission TryReserve(AutomaticIndexCandidate candidate, AutomaticIndexCost cost, bool visible)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (sync)
        {
            var collection = GetUsage(collectionUsage, candidate.Scope);
            var tenant = GetUsage(tenantUsage, candidate.Scope.Tenant);
            var collectionBudget = collectionBudgets.GetValueOrDefault(candidate.Scope, Unlimited);
            var tenantBudget = tenantBudgets.GetValueOrDefault(candidate.Scope.Tenant, Unlimited);
            var nextCollection = collection.Add(cost, visible);
            var nextTenant = tenant.Add(cost, visible);
            if (!Fits(nextCollection, collectionBudget))
            {
                return new BudgetAdmission(false, "collection budget exceeded", collection.ToRecord());
            }

            if (!Fits(nextTenant, tenantBudget))
            {
                return new BudgetAdmission(false, "tenant budget exceeded", collection.ToRecord());
            }

            collectionUsage[candidate.Scope] = nextCollection;
            tenantUsage[candidate.Scope.Tenant] = nextTenant;
            return new BudgetAdmission(true, null, nextCollection.ToRecord());
        }
    }

    /// <inheritdoc />
    public void Release(AutomaticIndexCandidate candidate, AutomaticIndexCost cost, bool visible)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (sync)
        {
            collectionUsage[candidate.Scope] = GetUsage(collectionUsage, candidate.Scope).Subtract(cost, visible);
            tenantUsage[candidate.Scope.Tenant] = GetUsage(tenantUsage, candidate.Scope.Tenant).Subtract(cost, visible);
        }
    }

    /// <inheritdoc />
    public AutomaticIndexUsage Usage(AutomaticIndexScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (sync)
        {
            return GetUsage(collectionUsage, scope).ToRecord();
        }
    }

    /// <summary>Converts a candidate's maintenance estimate to resource units.</summary>
    public static AutomaticIndexCost CostOf(AutomaticIndexCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var width = Math.Max(1UL, candidate.EstimatedMaintenanceUnits);
        return new AutomaticIndexCost(width, checked(width * 128), width);
    }

    private static bool Fits(MutableUsage usage, AutomaticIndexBudget budget) =>
        usage.CpuUnits <= budget.CpuUnits
        && usage.MemoryBytes <= budget.MemoryBytes
        && usage.IoUnits <= budget.IoUnits
        && usage.VisibleIndexes <= budget.MaxVisibleIndexes;

    private static MutableUsage GetUsage<T>(Dictionary<T, MutableUsage> values, T key)
        where T : notnull => values.GetValueOrDefault(key);

    private sealed record MutableUsage
    {
        public double CpuUnits { get; init; }

        public ulong MemoryBytes { get; init; }

        public double IoUnits { get; init; }

        public int VisibleIndexes { get; init; }

        public MutableUsage Add(AutomaticIndexCost cost, bool visible) => new()
        {
            CpuUnits = CpuUnits + cost.CpuUnits,
            MemoryBytes = MemoryBytes > ulong.MaxValue - cost.MemoryBytes ? ulong.MaxValue : MemoryBytes + cost.MemoryBytes,
            IoUnits = IoUnits + cost.IoUnits,
            VisibleIndexes = visible ? checked(VisibleIndexes + 1) : VisibleIndexes,
        };

        public MutableUsage Subtract(AutomaticIndexCost cost, bool visible) => new()
        {
            CpuUnits = Math.Max(0, CpuUnits - cost.CpuUnits),
            MemoryBytes = MemoryBytes < cost.MemoryBytes ? 0 : MemoryBytes - cost.MemoryBytes,
            IoUnits = Math.Max(0, IoUnits - cost.IoUnits),
            VisibleIndexes = visible ? Math.Max(0, VisibleIndexes - 1) : VisibleIndexes,
        };

        public AutomaticIndexUsage ToRecord() => new(CpuUnits, MemoryBytes, IoUnits, VisibleIndexes);
    }
}
