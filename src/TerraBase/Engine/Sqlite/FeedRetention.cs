using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Changes feed retention (docs/terrabase/multi-region-replication.md section 11): every hour, each client database's feed
/// events stamped more than 7 days ago go, in batches, and their origins' floors rise, so an older cursor gets 410.
/// </summary>
internal sealed class FeedRetention(SqliteStorageEngine engine) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
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
        var before = new Hlc((ulong)(engine.HostTime.GetUtcNow() - Retention).ToUnixTimeMilliseconds() << 16);
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
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
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A deleted, busy or closing database: the next pass tries again.
            }
        }
    }
}
