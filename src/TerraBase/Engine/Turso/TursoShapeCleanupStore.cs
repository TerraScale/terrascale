using TerraBase.Features.Shapes;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed class TursoShapeCleanupStore(TursoStorageEngine engine) : IShapeCleanupStore
{
    public ValueTask<ShapeCleanupAdmission> ReserveAsync(ShapeCleanupObject item, long now, CancellationToken cancellationToken) =>
        Valid(item) ? WriteAsync(transaction => transaction.ReserveShapeCleanupAsync(item, now, cancellationToken), cancellationToken)
            : throw new ArgumentException("shape cleanup inventory identity is invalid", nameof(item));

    public ValueTask<bool> PublishAsync(string key, long epoch, string keyId, long now, CancellationToken cancellationToken) =>
        WriteAsync(transaction => transaction.PublishShapeCleanupAsync(key, epoch, keyId, now, cancellationToken), cancellationToken);

    public async ValueTask ForgetAsync(string key, TenantContext database, CancellationToken cancellationToken) =>
        _ = await WriteAsync(transaction => transaction.ForgetShapeCleanupAsync(key, database, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async ValueTask<(IReadOnlySet<string> Live, bool Saturated)> LiveAsync(IReadOnlyList<string> keys, long now, CancellationToken cancellationToken) =>
        await ReadAsync(transaction => transaction.LiveShapeCleanupAsync(keys, now, cancellationToken), cancellationToken).ConfigureAwait(false);

    public ValueTask<bool> ProvesAsync(TenantContext database, string handle, long floor, long epoch, string keyId, long now,
        CancellationToken cancellationToken) =>
        ReadAsync(transaction => transaction.ProvesShapeCleanupAsync(database, handle, floor, epoch, keyId, now, cancellationToken), cancellationToken);

    public ValueTask<int> ActivateObjectAsync(string key, ShapeCleanupReason reason, long now, CancellationToken cancellationToken) =>
        WriteAsync(transaction => transaction.ActivateShapeObjectAsync(key, reason, now, cancellationToken), cancellationToken);

    public ValueTask<int> ActivateHandleAsync(TenantContext database, string handle, long floor, ShapeCleanupReason reason, long now,
        CancellationToken cancellationToken) =>
        BatchesAsync(transaction => transaction.ActivateShapeHandleAsync(database, handle, floor, reason, now, cancellationToken), int.MaxValue, cancellationToken);

    public ValueTask<int> ActivateDatabaseAsync(TenantContext database, long now, CancellationToken cancellationToken) =>
        BatchesAsync(transaction => transaction.ActivateShapeDatabaseAsync(database, now, cancellationToken), int.MaxValue, cancellationToken);

    public async ValueTask<IReadOnlyList<ShapeCleanupItem>> LeaseAsync(string lease, long now, CancellationToken cancellationToken)
    {
        List<ShapeCleanupItem> items = [];
        while (items.Count < ShapeCleanupLimits.WindowItems)
        {
            var batch = await WriteAsync(transaction => transaction.LeaseShapeCleanupAsync(lease, now, ShapeCleanupLimits.WindowItems - items.Count,
                cancellationToken), cancellationToken).ConfigureAwait(false);
            items.AddRange(batch);
            if (batch.Count < ShapeCleanupLimits.Transaction)
            {
                break;
            }
        }

        return items;
    }

    public async ValueTask<int> SaveAsync(string lease, IReadOnlyList<ShapeCleanupProgress> progress, long now, CancellationToken cancellationToken)
    {
        var saved = 0;
        foreach (var batch in progress.Chunk(ShapeCleanupLimits.Transaction))
        {
            saved += await WriteAsync(transaction => transaction.SaveShapeCleanupAsync(lease, batch, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    public ValueTask<int> CollectAsync(long now, CancellationToken cancellationToken) =>
        BatchesAsync(transaction => transaction.CollectShapeCleanupAsync(now, cancellationToken), ShapeCleanupLimits.WindowItems, cancellationToken);

    public async ValueTask RecoverAsync(CancellationToken cancellationToken) =>
        _ = await BatchesAsync(transaction => transaction.RecoverShapeCleanupAsync(cancellationToken), int.MaxValue, cancellationToken).ConfigureAwait(false);

    public ValueTask<ShapeCleanupStatus> StatusAsync(long now, CancellationToken cancellationToken) =>
        ReadAsync(transaction => transaction.ShapeCleanupStatusAsync(now, cancellationToken), cancellationToken);

    private static bool Valid(ShapeCleanupObject item) => ShapeCleanupLimits.Canonical(item.Key) && Id(item.Database.OrganizationId)
        && Id(item.Database.ProjectId) && Id(item.Database.DatabaseId) && item.FloorOffset >= 0 && item.Epoch >= 0 && Id(item.KeyId);

    private static bool Id(string? value) => Ulid.TryParse(value, out var id) && id.ToString() == value;

    private async ValueTask<int> BatchesAsync(Func<TursoStorageTransaction, ValueTask<int>> write, int limit, CancellationToken cancellationToken)
    {
        var total = 0;
        int changed;
        do
        {
            changed = await WriteAsync(write, cancellationToken).ConfigureAwait(false);
            total += changed;
        }
        while (changed == ShapeCleanupLimits.Transaction && total < limit);

        return total;
    }

    private async ValueTask<T> ReadAsync<T>(Func<TursoStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var system = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (TursoStorageTransaction)await system.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        return await read(transaction).ConfigureAwait(false);
    }

    private async ValueTask<T> WriteAsync<T>(Func<TursoStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        var system = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (TursoStorageTransaction)await system.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await write(transaction).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
