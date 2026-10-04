using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Features.Documents;

internal sealed class DocumentService(IStorageEngine engine) : IDocumentService
{
    public ValueTask<bool> CreateDatabaseAsync(DatabaseName database, CancellationToken cancellationToken) =>
        engine.CreateDatabaseAsync(database, cancellationToken);

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

    public async ValueTask<DocumentPage?> ListAsync(
        DatabaseName database, string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.ListAsync(collection, afterId, limit, maxBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<IStorageTransaction?> BeginAsync(DatabaseName database, bool write, CancellationToken cancellationToken)
    {
        var store = await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        return store is null ? null : await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
    }
}
