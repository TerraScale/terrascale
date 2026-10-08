using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;

namespace TerraBase.Features.Management;

/// <summary>Brings the management catalog to the version this build supports, or refuses an unsupported one (503).</summary>
internal interface ICatalogGuard
{
    /// <summary>Migrates once per process; throws a 503 <see cref="BadHttpRequestException"/> for a catalog this build cannot use.</summary>
    ValueTask EnsureCatalogAsync(CancellationToken cancellationToken);

    /// <summary>
    /// On a catalog follower, checks the replicated catalog version inside the reader's own <paramref name="transaction"/>, since
    /// replication can bring a version this build does not read (503); nothing on a primary, which checked when it migrated.
    /// </summary>
    ValueTask CheckReadAsync(IStorageTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// Typed JSON documents in the system database (<see cref="IStorageEngine.OpenSystemDatabaseAsync"/>), where the management
/// catalog lives.
/// Every write runs in one transaction, which the engine serializes per database, so checks and writes are atomic.
/// </summary>
/// <remarks>
/// State that belongs to one managed database lives in collections under <see cref="DatabaseState"/> and state
/// that belongs to one project under <see cref="ProjectState"/>; deleting the database or project deletes it.
/// </remarks>
internal sealed class SystemCatalog(IStorageEngine engine, ICatalogGuard guard)
{
    // Catalog documents are small; this only bounds a page if one ever is not.
    private const long MaxPageBytes = 1024 * 1024;

    /// <summary>Collection prefix for the state of one managed database.</summary>
    public static string DatabaseState(string databaseId) => $"databases/{databaseId}/";

    /// <summary>Collection prefix for the state of one project.</summary>
    public static string ProjectState(string projectId) => $"projects/{projectId}/";

    /// <summary>Reads a snapshot of the management catalog after <see cref="ICatalogGuard"/> accepted its version.</summary>
    public async ValueTask<T> ReadAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        await guard.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await ReadUncheckedAsync(async transaction =>
        {
            await guard.CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false);
            return await read(transaction).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes to the management catalog after <see cref="ICatalogGuard"/> accepted its version.</summary>
    public async ValueTask<T> WriteAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        await guard.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await WriteUncheckedAsync(write, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes catalog state with a disk-synced commit before acknowledging a peer batch.</summary>
    public async ValueTask<T> WriteDurableAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        await guard.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await WriteUncheckedAsync(write, cancellationToken, durable: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads without the catalog version check, for state that does not depend on the management catalog (operator API
    /// keys, and authenticating any key), so the data plane keeps working while the catalog needs attention.
    /// </summary>
    public async ValueTask<T> ReadUncheckedAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    /// <summary>Writes without the catalog version check (see <see cref="ReadUncheckedAsync"/>); a failure result simply writes nothing.</summary>
    public async ValueTask<T> WriteUncheckedAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken, bool durable = false)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = durable
            ? await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false)
            : await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await write(transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    public static async ValueTask<T?> GetAsync<T>(
        IStorageTransaction transaction, string collection, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        var document = await transaction.GetAsync(collection, id, cancellationToken).ConfigureAwait(false);
        return document is null ? null : JsonSerializer.Deserialize(document.Json.Span, type);
    }

    public static async ValueTask PutAsync<T>(
        IStorageTransaction transaction, string collection, string id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await transaction.PutAsync(collection, id, JsonSerializer.SerializeToUtf8Bytes(value, type), cancellationToken).ConfigureAwait(false);

    public static async ValueTask<bool> AnyAsync(IStorageTransaction transaction, string collection, CancellationToken cancellationToken) =>
        (await transaction.ListAsync(collection, afterId: null, limit: 1, MaxPageBytes, cancellationToken).ConfigureAwait(false)).Documents.Count > 0;

    /// <summary>One page in document id order; <paramref name="id"/> gives the cursor of the last item.</summary>
    public static async ValueTask<ManagementPage<T>> ListAsync<T>(
        IStorageTransaction transaction, string collection, string? after, int limit, JsonTypeInfo<T> type, Func<T, string> id, CancellationToken cancellationToken)
    {
        var page = await transaction.ListAsync(collection, after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        var items = page.Documents.Select(document => JsonSerializer.Deserialize(document.Json.Span, type)!).ToList();
        return new ManagementPage<T>(items, page.HasMore ? id(items[^1]) : null);
    }

    /// <summary>Every document of a collection, for collections small enough to read whole.</summary>
    public static async ValueTask<List<T>> ListAllAsync<T>(
        IStorageTransaction transaction, string collection, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? after = null;
        DocumentPage page;
        do
        {
            page = await transaction.ListAsync(collection, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Documents.Select(document => JsonSerializer.Deserialize(document.Json.Span, type)!));
            after = page.Documents.Count > 0 ? page.Documents[^1].Id : after;
        }
        while (page.HasMore);

        return items;
    }
}
