using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Features.Documents;

internal sealed class DocumentService(IStorageEngine engine) : IDocumentService
{
    public async ValueTask<bool> CreateDatabaseAsync(DatabaseName database, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, create: false, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        await engine.OpenDatabaseAsync(database, create: true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(key.Database, write: false, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<long?> PutAsync(DocumentKey key, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(key.Database, write: true, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            var version = await transaction.PutAsync(key.Collection, key.Id, json, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return version;
        }
    }

    public async ValueTask<bool> DeleteAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(key.Database, write: true, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return false;
        }

        await using (transaction.ConfigureAwait(false))
        {
            var deleted = await transaction.DeleteAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }
    }

    public async ValueTask<IReadOnlyList<StoredDocument>?> ListAsync(
        DatabaseName database, string collection, string? afterId, int limit, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.ListAsync(collection, afterId, limit, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<IStorageTransaction?> BeginAsync(DatabaseName database, bool write, CancellationToken cancellationToken)
    {
        var store = await engine.OpenDatabaseAsync(database, create: false, cancellationToken).ConfigureAwait(false);
        return store is null ? null : await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
    }
}
