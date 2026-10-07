using TerraBase.Engine;
using TerraBase.Features.Management.Schema;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed class UsageService(IStorageEngine engine, IManagementService management, ISchemaService schema, UsageMeter meter, TimeProvider time)
    : IUsageService
{
    // Databases or projects one catalog read lists.
    private const int PageSize = 10;

    /// <summary>Databases one project usage request reads at most; a larger one's usage is too expensive.</summary>
    internal const int MaxDatabases = 100;

    /// <summary>
    /// After this long (the deadline of one query) a project usage request starts no more counts. Each
    /// database's count runs under its own query budget.
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private void ThrowPastDeadline(long started)
    {
        if (time.GetElapsedTime(started) >= Deadline)
        {
            throw new QueryCapacityException("usage exceeded its execution budget");
        }
    }

    public ValueTask<DatabaseUsage?> GetDatabaseUsageAsync(TenantContext database, UsageWindow window, CancellationToken cancellationToken) =>
        DatabaseAsync(database, window, budget: null, cancellationToken);

    // Within a project request, each step (the size lookup, the count, waiting for a flush) can end past
    // the deadline: then no further step starts, and the request answers too expensive.
    private async ValueTask<DatabaseUsage?> DatabaseAsync(TenantContext database, UsageWindow window, Budget? budget, CancellationToken cancellationToken)
    {
        Action? proceed = budget is null ? null : () => ThrowPastDeadline(budget.Started);

        // Membership was checked before; a database deleted since has no size or no storage to count.
        var bytes = await engine.GetDatabaseSizeAsync(database, cancellationToken).ConfigureAwait(false);
        proceed?.Invoke();
        var collections = bytes is null ? null : await schema.CountCollectionsAsync(database, cancellationToken).ConfigureAwait(false);
        proceed?.Invoke();
        if (bytes is not { } size || collections is null)
        {
            return null;
        }

        var (lifetime, buckets) = await meter.GetAsync(database, window, cancellationToken, proceed).ConfigureAwait(false);
        var series = buckets.Select((counts, index) => new UsagePoint(window.Granularity.Start(window.First + index), counts.Reads, counts.Writes)).ToArray();
        return new DatabaseUsage(
            database.DatabaseId!, size, collections.Sum(collection => collection.Bytes), collections.Count, collections.Sum(collection => collection.Documents),
            lifetime.Reads, lifetime.Writes, Period(window, series), series);
    }

    // The request is bounded as a whole: every database is listed (and the request refused past MaxDatabases) before any
    // count, and after Deadline no further catalog read or count starts (one already running ends within its own budget).
    public async ValueTask<ProjectUsage?> GetProjectUsageAsync(
        string userId, string organizationId, string projectId, UsageWindow window, CancellationToken cancellationToken)
    {
        var budget = new Budget(time.GetTimestamp());
        return await ListDatabasesAsync(userId, organizationId, projectId, budget, cancellationToken).ConfigureAwait(false) is { } databases
            ? await ProjectAsync(userId, organizationId, projectId, databases, window, budget, cancellationToken).ConfigureAwait(false)
            : null;
    }

    // Lists every database of a project, counting them toward MaxDatabases; null when the project does not exist.
    private async ValueTask<List<ManagedDatabase>?> ListDatabasesAsync(
        string userId, string organizationId, string projectId, Budget budget, CancellationToken cancellationToken)
    {
        var databases = new List<ManagedDatabase>();
        string? after = null;
        do
        {
            ThrowPastDeadline(budget.Started);
            var page = await management.ListDatabasesAsync(userId, organizationId, projectId, after, PageSize, cancellationToken).ConfigureAwait(false);
            // Also after the call, whatever it found: a slow last page leaves no count to check it before.
            ThrowPastDeadline(budget.Started);
            if (page is null)
            {
                return null;
            }

            databases.AddRange(page.Items);
            Count(budget, page.Items.Count);
            after = page.Next;
        }
        while (after is not null);

        return databases;
    }

    private async ValueTask<ProjectUsage> ProjectAsync(
        string userId, string organizationId, string projectId, List<ManagedDatabase> databases, UsageWindow window, Budget budget,
        CancellationToken cancellationToken)
    {
        var items = new List<DatabaseUsage>(databases.Count);
        foreach (var database in databases)
        {
            ThrowPastDeadline(budget.Started);
            // Each database's tenant comes from its own membership-checked read; one deleted meanwhile is left out. The
            // read can itself outlast the deadline, so it is checked again before the count starts.
            if (await management.GetDatabaseTenantAsync(userId, organizationId, projectId, database.Id, cancellationToken).ConfigureAwait(false) is { } tenant)
            {
                ThrowPastDeadline(budget.Started);
                if (await DatabaseAsync(tenant, window, budget, cancellationToken).ConfigureAwait(false) is { } usage)
                {
                    items.Add(usage);
                }
            }
        }

        // The last tenant lookup or usage read can itself end past the deadline.
        ThrowPastDeadline(budget.Started);
        var series = Sum(window, items.Select(item => item.Series));
        return new ProjectUsage(
            projectId, items.Count, items.Sum(item => item.StorageBytes), items.Sum(item => item.LogicalBytes), items.Sum(item => item.Documents),
            items.Sum(item => item.Reads), items.Sum(item => item.Writes), Period(window, series), series, [.. items.Select(item => item with { Series = [] })]);
    }

    private static void Count(Budget budget, int listed)
    {
        budget.Listed += listed;
        if (budget.Listed > MaxDatabases)
        {
            throw new QueryCapacityException("usage covers more databases than one request reads");
        }
    }

    private static UsagePoint[] Sum(UsageWindow window, IEnumerable<IReadOnlyList<UsagePoint>> series)
    {
        var sum = new UsagePoint[window.Count];
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] = new UsagePoint(window.Granularity.Start(window.First + i), 0, 0);
        }

        foreach (var points in series)
        {
            for (var i = 0; i < points.Count; i++)
            {
                sum[i] = sum[i] with { Reads = sum[i].Reads + points[i].Reads, Writes = sum[i].Writes + points[i].Writes };
            }
        }

        return sum;
    }

    private static UsagePeriod Period(UsageWindow window, IReadOnlyList<UsagePoint> series) =>
        new(window.From, window.To, window.Granularity.Name, series.Sum(point => point.Reads), series.Sum(point => point.Writes));

    // What one project usage request has spent.
    private sealed class Budget(long started)
    {
        public long Started { get; } = started;

        public int Listed { get; set; }
    }
}
