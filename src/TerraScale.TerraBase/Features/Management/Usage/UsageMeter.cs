using System.Collections.Concurrent;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;
using static TerraScale.TerraBase.Features.Management.SystemCatalog;

namespace TerraScale.TerraBase.Features.Management.Usage;

/// <summary>Operation counts; persisted per managed database as document <c>ops</c> of its <c>usage</c> state collection.</summary>
/// <param name="Reads">Document reads.</param>
/// <param name="Writes">Document writes.</param>
internal sealed record OpCounts(long Reads, long Writes)
{
    public static OpCounts Zero { get; } = new(0, 0);

    public static OpCounts operator +(OpCounts left, OpCounts right)
    {
        return new(left.Reads + right.Reads, left.Writes + right.Writes);
    }

    public static OpCounts operator -(OpCounts left, OpCounts right)
    {
        return new(left.Reads - right.Reads, left.Writes - right.Writes);
    }
}

/// <summary>
/// Counts operations in memory per database (organization, project and database: the request's tenant) and adds them to
/// the catalog on <see cref="FlushAsync"/>. Counts of a database the catalog no longer names are dropped at the flush.
/// </summary>
/// <remarks>
/// ponytail: memory holds one entry per database with operations since the last flush, so it is bounded by the
/// number of databases; per-database caps if that ever grows unbounded.
/// </remarks>
internal sealed partial class UsageMeter(SystemCatalog catalog, ILogger<UsageMeter> logger) : IUsageMeter, IDisposable
{
    private const string Ops = "ops";

    // The id of the last flush that added to a database's counts, saved in the same write.
    private const string LastFlush = "flush";

    private static readonly OpCounts Read = new(1, 0);

    // Best-effort dashboard usage, not billing: counts live only here until a flush saves them, so a crash loses at most
    // those counted since the last successful flush. A failed flush keeps them for the next; a clean shutdown flushes.
    private readonly ConcurrentDictionary<DatabaseOwner, OpCounts> _pending = new();

    // Makes a flush (catalog commit plus removal from memory) atomic for readers, so totals never count twice or miss.
    private readonly SemaphoreSlim _flush = new(1, 1);

    // A flush whose write threw may still have committed (the catalog commits before it disposes the transaction): its
    // counts stay pending until its id, saved with them, shows whether they were saved. Guarded by _flush.
    private (string Id, KeyValuePair<DatabaseOwner, OpCounts>[] Batch)? _inDoubt;

    public void RecordRead(TenantContext database) => Add(DatabaseOwner.Of(database), Read);

    public void RecordWrites(TenantContext database, int writes) => Add(DatabaseOwner.Of(database), new OpCounts(0, writes));

    /// <summary>Persisted plus not yet flushed counts.</summary>
    public async ValueTask<OpCounts> GetAsync(TenantContext database, CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var persisted = await catalog.ReadAsync(
                transaction => SystemCatalog.GetAsync(transaction, Collection(database.DatabaseId!), Ops, TerraBaseJsonContext.Default.OpCounts, cancellationToken),
                cancellationToken).ConfigureAwait(false) ?? OpCounts.Zero;
            return _pending.TryGetValue(DatabaseOwner.Of(database), out var pending) ? persisted + pending : persisted;
        }
        finally
        {
            _flush.Release();
        }
    }

    /// <summary>Adds pending counts to the catalog in one transaction. On failure the counts stay pending.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        // The snapshot is taken under the lock, so overlapping flushes never persist the same counts twice.
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flush.Release();
        }
    }

    private async ValueTask FlushLockedAsync(CancellationToken cancellationToken)
    {
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        var batch = _pending.ToArray();
        if (batch.Length == 0)
        {
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        int saved;
        try
        {
            saved = await WriteAsync(id, batch, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _inDoubt = (id, batch);
            throw;
        }

        foreach (var (database, counts) in batch)
        {
            Subtract(database, counts);
        }

        if (saved < batch.Length)
        {
            LogDropped(logger, batch.Length - saved);
        }
    }

    // Decides a flush that threw: it committed if any database it saved holds its id (one transaction saves all or none),
    // and then its counts leave memory; otherwise they stay pending for the next flush. A failed read leaves it in doubt.
    private async ValueTask SettleAsync(CancellationToken cancellationToken)
    {
        if (_inDoubt is not { } doubt)
        {
            return;
        }

        var committed = await catalog.ReadAsync(async transaction =>
        {
            foreach (var (database, _) in doubt.Batch)
            {
                if (await SystemCatalog.GetAsync(transaction, Collection(database.DatabaseId), LastFlush, TerraBaseJsonContext.Default.String, cancellationToken)
                    .ConfigureAwait(false) == doubt.Id)
                {
                    return true;
                }
            }

            return false;
        }, cancellationToken).ConfigureAwait(false);
        if (committed)
        {
            foreach (var (database, counts) in doubt.Batch)
            {
                Subtract(database, counts);
            }
        }

        _inDoubt = null;
    }

    private ValueTask<int> WriteAsync(string id, KeyValuePair<DatabaseOwner, OpCounts>[] batch, CancellationToken cancellationToken) =>
        catalog.WriteAsync(async transaction =>
        {
            var saved = 0;
            foreach (var (database, counts) in batch)
            {
                // Checked inside the write, which the engine serializes with a database delete: a delete that commits
                // later removes these counts with the database's state; one that committed earlier removed the database
                // from its project. Either way a deleted database gets no usage back. One point read per level.
                if (await EngineManagementService.DatabaseTenantUncheckedAsync(
                    transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
                {
                    continue;
                }

                var collection = Collection(database.DatabaseId);
                var persisted = await SystemCatalog.GetAsync(transaction, collection, Ops, TerraBaseJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, collection, Ops, (persisted ?? OpCounts.Zero) + counts, TerraBaseJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, collection, LastFlush, id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
                saved++;
            }

            return saved;
        }, cancellationToken);

    public void Dispose() => _flush.Dispose();

    private static string Collection(string databaseId) => DatabaseState(databaseId) + "usage";

    private void Add(DatabaseOwner database, OpCounts counts) => _pending.AddOrUpdate(database, counts, (_, current) => current + counts);

    // Lock-free: retries when a concurrent Add changed the entry; removes it once nothing is pending.
    private void Subtract(DatabaseOwner database, OpCounts counts)
    {
        while (_pending.TryGetValue(database, out var current))
        {
            var rest = current - counts;
            if (rest == OpCounts.Zero ? _pending.TryRemove(KeyValuePair.Create(database, current)) : _pending.TryUpdate(database, rest, current))
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped operation counts of {Databases} deleted databases")]
    private static partial void LogDropped(ILogger logger, int databases);
}
