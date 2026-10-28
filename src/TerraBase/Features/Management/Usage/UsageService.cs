using System.Diagnostics;
using System.Globalization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Schema;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed class UsageService(IStorageEngine engine, IManagementService management, ISchemaService schema, UsageMeter meter, TimeProvider time, SystemCatalog catalog, Features.FileStorage.IFileStorageUsage files)
    : IUsageService
{
    /// <summary>
    /// What <see cref="AllowanceUsage.Operations"/> counts: 4 KiB operation units, each read and write once in the region
    /// that served it (replication is not billed), summed over every region and node.
    /// </summary>
    internal const string OperationsContract = "operation-units-4kib-global-v1";

    private const int AllowancePageSize = 25;

    // One allowance page reads at most this long; a slower one answers 503 and the caller retries.
    private static readonly TimeSpan AllowanceScanBudget = TimeSpan.FromSeconds(30);

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
        var month = time.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (await meter.GetMonthAsync(organizationId, projectId, cancellationToken).ConfigureAwait(false) is not UsageTotals project
            || await meter.GetMonthAsync(organizationId, projectId: null, cancellationToken).ConfigureAwait(false) is not UsageTotals organization)
        {
            return new Unavailable("usage accounting is unavailable; retry");
        }

        var series = Sum(window, items.Select(item => item.Series));
        var fileBytes = await files.BytesAsync(organizationId, projectId, cancellationToken).ConfigureAwait(false);
        return new ProjectUsage(
            projectId, items.Count, items.Sum(item => item.Collections), checked(items.Sum(item => item.StorageBytes) + fileBytes), checked(items.Sum(item => item.LogicalBytes) + fileBytes), items.Sum(item => item.Documents),
            items.Sum(item => item.Reads), items.Sum(item => item.Writes), Period(window, series), series, [.. items.Select(item => item with { Series = [] })],
            new UsageMonth(month, project.Reads, project.Writes, project.Reads + project.Writes, organization.Reads + organization.Writes))
        { FileBytes = fileBytes };
    }

    public async ValueTask<AllowanceUsageResult> ListAllowanceUsageAsync(string? after, CancellationToken cancellationToken)
    {
        // The page is labelled with the month and instant it started in; a scan that ends in another month is refused.
        var observedAt = time.GetUtcNow();
        var month = new DateTimeOffset(observedAt.Year, observedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
        using var budget = new CancellationTokenSource(AllowanceScanBudget, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            // The catalog keeps one document per organization, named by its id.
            var listed = await catalog.ReadAsync<OrganizationPage>(async transaction =>
            {
                var page = await transaction.ListAsync("organizations", after, AllowancePageSize, 1024 * 1024, bounded.Token).ConfigureAwait(false);
                return new OrganizationIds([.. page.Documents.Select(document => document.Id)], page.HasMore);
            }, static error => error, bounded.Token).ConfigureAwait(false);
            if (listed is not OrganizationIds organizations)
            {
                return listed switch
                {
                    Unavailable error => error,
                    OrganizationIds => throw new UnreachableException(),
                };
            }

            var items = new List<AllowanceUsage>(organizations.Ids.Count);
            foreach (var organizationId in organizations.Ids)
            {
                if (await meter.GetMonthAsync(organizationId, projectId: null, bounded.Token).ConfigureAwait(false) is not UsageTotals units)
                {
                    return new Unavailable("usage accounting is unavailable; retry");
                }

                items.Add(new AllowanceUsage(organizationId, checked(units.Reads + units.Writes), null));
            }

            var ended = time.GetUtcNow();
            if (ended.Year != month.Year || ended.Month != month.Month)
            {
                return new Unavailable("the UTC month changed during the usage scan; retry");
            }

            return new AllowanceUsagePage(month, observedAt, items,
                organizations.More && items.Count > 0 ? items[^1].OrganizationId : null, OperationsContract, "none");
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new Unavailable("the usage scan exceeded its time budget; retry");
        }
        catch (OverflowException)
        {
            return new Unavailable("an organization's usage exceeds the countable range");
        }
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

    private sealed record OrganizationIds(List<string> Ids, bool More);

    private readonly union OrganizationPage(OrganizationIds, Unavailable);

    // Every database of a project, or why they cannot be listed.
    private readonly union ProjectDatabases(List<ManagedDatabase>, NotFound, Unavailable);
}
