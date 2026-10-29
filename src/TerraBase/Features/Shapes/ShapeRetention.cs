using System.Buffers;
using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Shapes;

/// <summary>Local sealed data maintenance; FR-20 supplies stores and invokes one bounded pass at a time.</summary>
internal interface IShapeRetention
{
    ValueTask<long> GetFloorAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken);

    ValueTask<Conflict?> CheckOffsetAsync(IDatabaseStore store, string handle, long offset, CancellationToken cancellationToken);

    ValueTask<int> RunPassAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken);

    ValueTask<int> DropHandleAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken);
}

/// <summary>Storage operations kept on the engine's transaction runner, without exposing Turso to the feature.</summary>
internal interface IShapeRetentionTransaction
{
    ValueTask<long> ReadShapeFloorAsync(string handle, CancellationToken cancellationToken);

    ValueTask<int> TrimShapeAsync(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken);

    ValueTask<int> DropShapeAsync(string handle, int maxRows, long maxBytes, CancellationToken cancellationToken);
}

internal sealed class ShapeRetention(TimeProvider time, IShapeStorage storage,
    Func<IDatabaseStore, string, long, CancellationToken, ValueTask>? onTrimmed = null) : IShapeRetention
{
    private static readonly SearchValues<char> HandleHex = SearchValues.Create("0123456789abcdef");
    internal const int BatchRows = 1024;
    internal const long BatchBytes = 8 * 1024 * 1024;

    public async ValueTask<long> GetFloorAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken)
    {
        Validate(store, handle);
        await using var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        return await Storage(transaction).ReadShapeFloorAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Conflict?> CheckOffsetAsync(IDatabaseStore store, string handle, long offset, CancellationToken cancellationToken) =>
        offset < await GetFloorAsync(store, handle, cancellationToken).ConfigureAwait(false)
            ? new Conflict("The shape offset is below the retention floor.", "must_refetch")
            : null;

    public async ValueTask<int> RunPassAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken)
    {
        Validate(store, handle);
        var before = (time.GetUtcNow() - FeedCursor.Retention).ToUnixTimeMilliseconds();
        int deleted;
        await using (var transaction = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false))
        {
            deleted = await Storage(transaction).TrimShapeAsync(handle, before, BatchRows, BatchBytes, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (onTrimmed is not null)
        {
            // The floor is read after the trim's writer is disposed; the callback's failures belong to its owner.
            await onTrimmed(store, handle, await GetFloorAsync(store, handle, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        return deleted;
    }

    public async ValueTask<int> DropHandleAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken)
    {
        Validate(store, handle);
        int deleted;
        await using (var transaction = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false))
        {
            deleted = await Storage(transaction).DropShapeAsync(handle, BatchRows, BatchBytes, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (onTrimmed is not null)
        {
            await onTrimmed(store, handle, long.MaxValue, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    private static IShapeRetentionTransaction Storage(IStorageTransaction transaction) =>
        transaction as IShapeRetentionTransaction ?? throw new NotSupportedException("The storage engine does not support shape retention.");

    private static void Validate(IDatabaseStore store, string handle)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(handle);
        if (store.Tenant is null || handle.Length != 19 || !handle.StartsWith("h1-", StringComparison.Ordinal)
            || handle.AsSpan(3).ContainsAnyExcept(HandleHex))
        {
            throw new ArgumentException("Shape retention requires a client database and a canonical v1 handle.", nameof(handle));
        }
    }
}
