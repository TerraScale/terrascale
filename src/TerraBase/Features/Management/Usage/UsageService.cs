using System.Diagnostics;
using System.Globalization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed class UsageService(IStorageEngine engine, IManagementService management, UsageMeter meter, TimeProvider time, SystemCatalog catalog, Features.FileStorage.IFileStorageUsage files)
    : IUsageService
{
    /// <summary>
    /// What <see cref="AllowanceUsage.Operations"/> counts: write units, one per changed row and one per started 4 KiB the
    /// write grew the database by, whichever is more, summed over every region and node. Reads are not billed.
    /// </summary>
    internal const string OperationsContract = "write-units-4kib-global-v2";

    internal const string StorageContract = "stored-byte-hours-global-v1";

    internal const int AllowancePageSize = 25;

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
            database.DatabaseId!, size,
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
            projectId, items.Count, checked(items.Sum(item => item.StorageBytes) + fileBytes),
            items.Sum(item => item.Reads), items.Sum(item => item.Writes), Period(window, series), series, [.. items.Select(item => item with { Series = [] })],
            new UsageMonth(month, project.Reads, project.Writes, project.Writes, organization.Writes, organization.Reads))
        { FileBytes = fileBytes };
    }

    public async ValueTask<AllowanceUsageResult> ListAllowanceUsageAsync(string? after, bool previous, CancellationToken cancellationToken)
    {
        var observedAt = time.GetUtcNow();
        var current = new DateTimeOffset(observedAt.Year, observedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var month = previous ? current.AddMonths(-1) : current;
        using var budget = new CancellationTokenSource(AllowanceScanBudget, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            var listed = await catalog.ReadAsync<OrganizationPage>(async transaction =>
            {
                var page = await CatalogTables.OrganizationIdsAsync(transaction, after, AllowancePageSize, bounded.Token).ConfigureAwait(false);
                return new OrganizationIds([.. page.Items], page.Next is not null);
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
                if (await meter.GetMonthAsync(organizationId, projectId: null, bounded.Token, month).ConfigureAwait(false) is not UsageTotals units)
                {
                    return new Unavailable("usage accounting is unavailable; retry");
                }

                items.Add(new AllowanceUsage(organizationId, units.Writes, null, units.Reads, units.StorageByteHours));
            }

            var ended = time.GetUtcNow();
            if (ended.Year != current.Year || ended.Month != current.Month)
            {
                return new Unavailable("the UTC month changed during the usage scan; retry");
            }

            return new AllowanceUsagePage(month, observedAt, items,
                organizations.More && items.Count > 0 ? items[^1].OrganizationId : null, OperationsContract, StorageContract);
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
