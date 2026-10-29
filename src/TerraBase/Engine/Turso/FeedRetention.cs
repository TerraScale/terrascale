using TerraBase.Engine.Lattices;
using TerraBase.Features.Catalog;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed class FeedRetention(TursoStorageEngine engine, ILogger<FeedRetention> logger, ICatalogGate? gate = null) : BackgroundService
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

    internal async Task TrimAsync(CancellationToken cancellationToken)
    {
        if (gate is { IsOpen: false })
        {
            return;
        }

        var before = FeedCursor.At(engine.HostTime.GetUtcNow() - FeedCursor.Retention);
        if (engine.SystemDatabaseExists)
        {
            try
            {
                var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
                await TrimStoreAsync(system, before, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Local catalog history retention failed; the next pass retries");
            }
        }
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is TursoDatabaseStore store)
                {
                    await TrimStoreAsync(store, before, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Local history retention failed; the next pass retries");
            }
        }
    }

    private static async Task TrimStoreAsync(TursoDatabaseStore store, Hlc before, CancellationToken cancellationToken)
    {
        int collected;
        int trimmed;
        do
        {
            await using var transaction = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            collected = await transaction.CollectHistoryAsync(before, cancellationToken).ConfigureAwait(false);
            trimmed = store.IsSystem ? 0 : await transaction.TrimFeedAsync(before, Batch, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        while (collected == Batch || trimmed == Batch);
    }
}
