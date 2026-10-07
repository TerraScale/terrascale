using TerraBase.Engine;
using TerraBase.Features.Management.Schema;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed class UsageService(IStorageEngine engine, IManagementService management, ISchemaService schema, UsageMeter meter, TimeProvider time)
    : IUsageService
{
    // Databases one catalog read lists.
    private const int PageSize = 10;

    /// <summary>Databases one project usage request reads at most; a larger project's usage is too expensive.</summary>
    internal const int MaxDatabases = 100;

    /// <summary>
    /// After this long (the deadline of one query) a project usage request starts no more counts. Each database's count
    /// runs under its own query budget.
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private void ThrowPastDeadline(long started)
    {
        if (time.GetElapsedTime(started) >= Deadline)
        {
            throw new QueryCapacityException("project usage exceeded its execution budget");
        }
    }

    public async ValueTask<DatabaseUsage?> GetDatabaseUsageAsync(TenantContext database, CancellationToken cancellationToken)
    {
        // Membership was checked before; a database deleted since has no size or no storage to count.
        if (await engine.GetDatabaseSizeAsync(database, cancellationToken).ConfigureAwait(false) is not { } bytes
            || await schema.CountCollectionsAsync(database, cancellationToken).ConfigureAwait(false) is not { } collections)
        {
            return null;
        }

        var ops = await meter.GetAsync(database, cancellationToken).ConfigureAwait(false);
        return new DatabaseUsage(database.DatabaseId!, bytes, collections.Count, collections.Sum(collection => collection.Documents), ops.Reads, ops.Writes);
    }

    // The request is bounded as a whole: a project of more than MaxDatabases databases is refused before any count, and
    // after Deadline no further catalog read or count starts (one already running ends within its own query budget).
    public async ValueTask<ProjectUsage?> GetProjectUsageAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var databases = new List<ManagedDatabase>();
        string? after = null;
        do
        {
            if (await management.ListDatabasesAsync(userId, organizationId, projectId, after, PageSize, cancellationToken).ConfigureAwait(false) is not { } page)
            {
                return null;
            }

            ThrowPastDeadline(started);
            databases.AddRange(page.Items);
            if (databases.Count > MaxDatabases)
            {
                throw new QueryCapacityException("the project has more databases than one usage request reads");
            }

            after = page.Next;
        }
        while (after is not null);

        var items = new List<DatabaseUsage>(databases.Count);
        foreach (var database in databases)
        {
            ThrowPastDeadline(started);
            // Each database's tenant comes from its own membership-checked read; one deleted meanwhile is left out. The
            // read can itself outlast the deadline, so it is checked again before the count starts.
            if (await management.GetDatabaseTenantAsync(userId, organizationId, projectId, database.Id, cancellationToken).ConfigureAwait(false) is { } tenant)
            {
                ThrowPastDeadline(started);
                if (await GetDatabaseUsageAsync(tenant, cancellationToken).ConfigureAwait(false) is { } usage)
                {
                    items.Add(usage);
                }
            }
        }

        return new ProjectUsage(
            projectId,
            items.Count,
            items.Sum(item => item.StorageBytes),
            items.Sum(item => item.Documents),
            items.Sum(item => item.Reads),
            items.Sum(item => item.Writes),
            items);
    }
}
