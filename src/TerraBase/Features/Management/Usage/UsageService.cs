using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
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

    private static readonly Validation QueryTooExpensive = new("query too expensive", "invalid_query");

    public async ValueTask<DatabaseUsageResult> GetDatabaseUsageAsync(TenantContext database, UsageWindow window, CancellationToken cancellationToken)
    {
        try
        {
            return await DatabaseAsync(database, window, budget: null, cancellationToken).ConfigureAwait(false);
        }
        catch (QueryCapacityException)
        {
            return QueryTooExpensive;
        }
    }

    // Within a project request, each step (the size lookup, the count, waiting for a flush) can end past
    // the deadline: then no further step starts, and the request answers too expensive.
    private async ValueTask<DatabaseUsageResult> DatabaseAsync(TenantContext database, UsageWindow window, Budget? budget, CancellationToken cancellationToken)
    {
        Action? proceed = budget is null ? null : () => ThrowPastDeadline(budget.Started);

        // Membership was checked before; a database deleted since has no size or no storage to count.
        var bytes = await engine.GetDatabaseSizeAsync(database, cancellationToken).ConfigureAwait(false);
        proceed?.Invoke();
        if (bytes is not { } size)
        {
            return new NotFound();
        }

        var counted = await schema.CountCollectionsAsync(database, cancellationToken).ConfigureAwait(false);
        proceed?.Invoke();
        if (counted is not IReadOnlyList<CollectionStats> collections)
        {
            return counted switch
            {
                NotFound error => error,
                Validation error => error,
                IReadOnlyList<CollectionStats> => throw new UnreachableException(),
            };
        }

        var counts = await meter.GetAsync(database, window, cancellationToken, proceed).ConfigureAwait(false);
        proceed?.Invoke();
        if (counts is not UsageCounts saved)
        {
            return counts switch
            {
                Unavailable error => error,
                UsageCounts => throw new UnreachableException(),
            };
        }

        var (lifetime, buckets) = saved;
        var series = buckets.Select((counts, index) => new UsagePoint(window.Granularity.Start(window.First + index), counts.Reads, counts.Writes)).ToArray();
        return new DatabaseUsage(
            database.DatabaseId!, size, collections.Sum(collection => collection.Bytes), collections.Count, collections.Sum(collection => collection.Documents),
            lifetime.Reads, lifetime.Writes, Period(window, series), series);
    }

    // The request is bounded as a whole: every database is listed (and the request refused past MaxDatabases) before any
    // count, and after Deadline no further catalog read or count starts (one already running ends within its own budget).
    public async ValueTask<ProjectUsageResult> GetProjectUsageAsync(
        string userId, string organizationId, string projectId, UsageWindow window, CancellationToken cancellationToken)
    {
        try
        {
            var budget = new Budget(time.GetTimestamp());
            return await ListDatabasesAsync(userId, organizationId, projectId, budget, cancellationToken).ConfigureAwait(false) switch
            {
                List<ManagedDatabase> databases => await ProjectAsync(userId, organizationId, projectId, databases, window, budget, cancellationToken).ConfigureAwait(false),
                NotFound error => error,
                Unavailable error => error,
            };
        }
        catch (QueryCapacityException)
        {
            return QueryTooExpensive;
        }
    }

    // Lists every database of a project, counting them toward MaxDatabases.
    private async ValueTask<ProjectDatabases> ListDatabasesAsync(
        string userId, string organizationId, string projectId, Budget budget, CancellationToken cancellationToken)
    {
        var databases = new List<ManagedDatabase>();
        string? after = null;
        do
        {
            ThrowPastDeadline(budget.Started);
            var listed = await management.ListDatabasesAsync(userId, organizationId, projectId, after, PageSize, cancellationToken).ConfigureAwait(false);
            // Also after the call, whatever it found: a slow last page leaves no count to check it before.
            ThrowPastDeadline(budget.Started);
            if (listed is not ManagementPage<ManagedDatabase> page)
            {
                return listed switch
                {
                    NotFound error => error,
                    Unavailable error => error,
                    ManagementPage<ManagedDatabase> => throw new UnreachableException(),
                };
            }

            databases.AddRange(page.Items);
            Count(budget, page.Items.Count);
            after = page.Next;
        }
        while (after is not null);

        return databases;
    }

    private async ValueTask<ProjectUsageResult> ProjectAsync(
        string userId, string organizationId, string projectId, List<ManagedDatabase> databases, UsageWindow window, Budget budget,
        CancellationToken cancellationToken)
    {
        var items = new List<DatabaseUsage>(databases.Count);
        foreach (var database in databases)
        {
            ThrowPastDeadline(budget.Started);
            // Each database's tenant comes from its own membership-checked read; one deleted meanwhile is left out. The
            // read can itself outlast the deadline, so it is checked again before the count starts.
            var found = await management.GetDatabaseTenantAsync(userId, organizationId, projectId, database.Id, cancellationToken).ConfigureAwait(false);
            ThrowPastDeadline(budget.Started);
            if (found is Unavailable unavailable)
            {
                return unavailable;
            }

            if (found is TenantContext tenant)
            {
                // A database deleted meanwhile (NotFound) is left out.
                var read = await DatabaseAsync(tenant, window, budget, cancellationToken).ConfigureAwait(false);
                if (read is NotFound)
                {
                    continue;
                }

                if (read is not DatabaseUsage usage)
                {
                    return read switch
                    {
                        Validation error => error,
                        Unavailable error => error,
                        DatabaseUsage or NotFound => throw new UnreachableException(),
                    };
                }

                items.Add(usage);
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

    // Every database of a project, or why they cannot be listed.
    private readonly union ProjectDatabases(List<ManagedDatabase>, NotFound, Unavailable);
}
