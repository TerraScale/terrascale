using TerraBase.Configuration;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Documents;

/// <summary>
/// Collection TTL (docs/terrabase/collection-ttl.md): every hour, each database homed in this region has its expired documents
/// deleted, replicating as ordinary deletes, so a document is gone at most about an hour after its expiry time.
/// </summary>
internal sealed class CollectionTtlSweeper(ITenantDirectory tenants, DocumentService documents, TerraBaseOptions options, TimeProvider time,
    ILogger<CollectionTtlSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    // shortcut: walks the whole catalog and scans each TTL collection by filter every hour; index the TTL member of large
    // collections, or keep a set of TTL collections, once a pass exceeds its query budget.
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var deleted = 0;
        TenantContext? after = null;
        do
        {
            if (await tenants.ReadDatabaseRecordsAsync(options.Region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                logger.ZLogWarning($"Collection TTL sweep skipped: the catalog is unreadable; the next pass retries");
                return deleted;
            }

            foreach (var database in page.Databases)
            {
                try
                {
                    deleted += await documents.SweepExpiredAsync(database, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    // A busy, deleted or too large database: the next pass tries again.
                    logger.ZLogWarning(error, $"Collection TTL sweep of database {database.DatabaseId:@DatabaseId} failed; the next pass retries");
                }
            }

            after = page.Next;
        }
        while (after is not null);

        return deleted;
    }
}
