using System.Threading.Channels;
using TerraBase.Engine;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management.Indexes;

/// <summary>
/// Adds a new vector index's existing documents in small write transactions, so writers wait for one batch at most
/// (docs/terrabase/vector-index.md). Creating an index here starts its build; each node also resumes builds at start and
/// every hour, which covers restarts and indexes that arrived by replication.
/// </summary>
internal sealed class VectorIndexBuilder(IStorageEngine engine, TimeProvider time, ILogger<VectorIndexBuilder> logger) : BackgroundService
{
    // A full queue drops the signal: the hourly pass builds that database.
    private readonly Channel<TenantContext> _pending = Channel.CreateBounded<TenantContext>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Start(TenantContext database) => _pending.Writer.TryWrite(database);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweeps = SweepAsync(stoppingToken);
        await foreach (var database in _pending.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await BuildAsync(database, stoppingToken).ConfigureAwait(false);
        }

        await sweeps.ConfigureAwait(false);
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } read)
                    {
                        continue;
                    }

                    await using (read.ConfigureAwait(false))
                    {
                        if ((await read.ListIndexesAsync(cancellationToken).ConfigureAwait(false)).Any(index => index.Building))
                        {
                            Start(database);
                        }
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Vector index check of database {database.DatabaseId:@DatabaseId} failed; the next pass retries");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    internal async Task BuildAsync(TenantContext database, CancellationToken cancellationToken)
    {
        try
        {
            var more = true;
            while (more && await engine.BeginTransactionAsync(database, write: true, cancellationToken).ConfigureAwait(false) is { } transaction)
            {
                await using (transaction.ConfigureAwait(false))
                {
                    more = await transaction.BuildVectorIndexAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // A busy, deleted or failing database: the hourly pass resumes from the last committed batch.
            logger.ZLogWarning(error, $"Vector index build of database {database.DatabaseId:@DatabaseId} stopped; the next pass resumes it");
        }
    }
}
