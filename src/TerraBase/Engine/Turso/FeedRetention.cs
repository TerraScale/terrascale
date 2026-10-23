using ZLogger;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Changes feed retention (docs/terrabase/multi-region-replication.md section 11): every hour, each client database's feed
/// events stamped more than 7 days ago go, in batches, and their origins' floors rise, so an older cursor gets 410.
/// </summary>
internal sealed class FeedRetention(TursoStorageEngine engine, ILogger<FeedRetention> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int Batch = 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, engine.HostTime);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await TrimAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    // shortcut: opens every database file each hour, as the CDC source's pass does; keep a set of databases with feed rows
    // once machines hold many databases.
    internal async Task TrimAsync(CancellationToken cancellationToken)
    {
        var before = FeedCursor.At(engine.HostTime.GetUtcNow() - FeedCursor.Retention);
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
                {
                    continue;
                }

                int deleted;
                do
                {
                    var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        deleted = await transaction.TrimFeedAsync(before, Batch, cancellationToken).ConfigureAwait(false);
                        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                while (deleted == Batch);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // A deleted, busy or closing database: the next pass tries again; one that keeps failing grows its feed.
                logger.ZLogWarning(error, $"Changes feed retention of a database failed; the next pass retries");
            }
        }
    }
}
