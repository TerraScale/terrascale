using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>Whether the management catalog may be used: <see cref="Unavailable"/> while it is at a version this build cannot use.</summary>
internal readonly union CatalogCheck(Done, Unavailable);

/// <summary>Initializes a fresh management catalog, or refuses an unsupported format (503).</summary>
internal interface ICatalogGuard
{
    /// <summary>Initializes a fresh primary catalog; <see cref="Unavailable"/> for a catalog this build cannot use.</summary>
    ValueTask<CatalogCheck> EnsureCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Checks the supported catalog version inside the operation's own read or write transaction.</summary>
    ValueTask<CatalogCheck> CheckReadAsync(IStorageTransaction transaction, CancellationToken cancellationToken);
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

    /// <summary>
    /// Reads a snapshot of the management catalog after <see cref="ICatalogGuard"/> accepted its version; otherwise the
    /// result is <paramref name="unavailable"/> of the refusal.
    /// </summary>
    public async ValueTask<T> ReadAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, Func<Unavailable, T> unavailable, CancellationToken cancellationToken)
    {
        var accepted = await guard.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (accepted is not Done)
        {
            return accepted switch
            {
                Done => throw new UnreachableException(),
                Unavailable refused => unavailable(refused),
            };
        }

        return await ReadUncheckedAsync(async transaction =>
            await guard.CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
            {
                Done => await read(transaction).ConfigureAwait(false),
                Unavailable stale => unavailable(stale),
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes to the management catalog after <see cref="ICatalogGuard"/> accepted its version (see <see cref="ReadAsync"/>).</summary>
    public async ValueTask<T> WriteAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, CancellationToken cancellationToken) =>
        await guard.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false) switch
        {
            Done => await WriteUncheckedAsync(async transaction =>
                await guard.CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => await write(transaction).ConfigureAwait(false),
                    Unavailable refused => unavailable(refused),
                }, cancellationToken).ConfigureAwait(false),
            Unavailable refused => unavailable(refused),
        };

    /// <summary>Whether the management catalog may be used now, for work that checks once before a pass of its own.</summary>
    public ValueTask<CatalogCheck> CheckAsync(CancellationToken cancellationToken) =>
        ReadAsync<CatalogCheck>(static _ => ValueTask.FromResult<CatalogCheck>(new Done()), static error => error, cancellationToken);

    /// <summary>Writes catalog state with a disk-synced commit before acknowledging a peer batch.</summary>
    public async ValueTask<T> WriteDurableAsync<T>(Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, CancellationToken cancellationToken) =>
        await guard.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false) switch
        {
            Done => await WriteUncheckedAsync(async transaction =>
                await guard.CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => await write(transaction).ConfigureAwait(false),
                    Unavailable refused => unavailable(refused),
                }, cancellationToken, durable: true).ConfigureAwait(false),
            Unavailable refused => unavailable(refused),
        };

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

    /// <summary>A bounded page of permanent purge authorities, including completed entries; null on catalog refusal.</summary>
    /// <remarks>
    /// An unreadable row (a replicated scalar or repeated key) is left out of the page, but the cursor moves past it so later
    /// purges still progress. It is never reported complete: convergence counts it as a pending purge error.
    /// </remarks>
    public ValueTask<ManagementPage<DatabasePurgeRecord>?> ListDatabasePurgesAsync(string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementPage<DatabasePurgeRecord>?>(async transaction =>
        {
            var page = await transaction.ListAsync(DatabasePurge.Collection, after, Math.Clamp(limit, 1, 100), MaxPageBytes, cancellationToken).ConfigureAwait(false);
            List<DatabasePurgeRecord> items = [];
            foreach (var document in page.Documents)
            {
                if (document.Json.Span.TrimStart(" \t\r\n"u8) is [(byte)'{', ..])
                {
                    try
                    {
                        if (EntityPayload.Read(document, TerraBaseJsonContext.Default.DatabasePurgeRecord) is { Database.DatabaseId: { } id } record && id == document.Id)
                        {
                            items.Add(record);
                        }
                    }
                    catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
                    {
                        // Left pending; see remarks.
                    }
                }
            }

            return new ManagementPage<DatabasePurgeRecord>(items, page.HasMore && page.Documents.Count > 0 ? page.Documents[^1].Id : null);
        }, static _ => null, cancellationToken);

    /// <summary>True only after every expected holder has published its own absence receipt.</summary>
    public ValueTask<bool> AreActiveCopiesAbsentAsync(TenantContext database, CancellationToken cancellationToken) =>
        ReadAsync<bool>(async transaction =>
            await GetAsync(transaction, DatabasePurge.Collection, database.DatabaseId!, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false) is { } record
            && DatabasePurge.SameDatabase(record.Database, database)
            && await DatabasePurge.AreActiveCopiesAbsentAsync(transaction, record, cancellationToken).ConfigureAwait(false), static _ => false, cancellationToken);

    /// <summary>Records verified backup absence without removing the permanent id fence or changing deadlines.</summary>
    public ValueTask<bool> MarkBackupVerifiedAsync(TenantContext database, DateTimeOffset verifiedAt, CancellationToken cancellationToken) =>
        WriteAsync<bool>(async transaction =>
        {
            var record = await GetAsync(transaction, DatabasePurge.Collection, database.DatabaseId!, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
            // Authority and receipts are read under this writer; verifiedAt is the verifier's clock, never compared to another node's.
            if (record is null || !DatabasePurge.SameDatabase(record.Database, database)
                || !await DatabasePurge.AreActiveCopiesAbsentAsync(transaction, record, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (record.BackupVerifiedAt is null)
            {
                await PutAsync(transaction, DatabasePurge.Collection, database.DatabaseId!, record with { BackupVerifiedAt = verifiedAt },
                    TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }, static _ => false, cancellationToken);

    public static async ValueTask<T?> GetAsync<T>(
        IStorageTransaction transaction, string collection, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        if (typeof(T) == typeof(ManagedDatabase) && await DatabasePurge.IsDeletedAsync(transaction, id, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var document = await transaction.GetAsync(collection, id, cancellationToken).ConfigureAwait(false);
        return document is null ? null : EntityPayload.Read(document, type);
    }

    public static async ValueTask PutAsync<T>(
        IStorageTransaction transaction, string collection, string id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await transaction.PutAsync(collection, id, EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(value, type), id, nested: true), cancellationToken).ConfigureAwait(false);

    public static async ValueTask<bool> AnyAsync(IStorageTransaction transaction, string collection, CancellationToken cancellationToken) =>
        (await transaction.ListAsync(collection, afterId: null, limit: 1, MaxPageBytes, cancellationToken).ConfigureAwait(false)).Documents.Count > 0;

    /// <summary>One page in document id order; <paramref name="id"/> gives the cursor of the last item.</summary>
    public static async ValueTask<ManagementPage<T>> ListAsync<T>(
        IStorageTransaction transaction, string collection, string? after, int limit, JsonTypeInfo<T> type, Func<T, string> id, CancellationToken cancellationToken)
    {
        var page = await transaction.ListAsync(collection, after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        var items = page.Documents.Select(document => EntityPayload.Read(document, type)!).ToList();
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
            items.AddRange(page.Documents.Select(document => EntityPayload.Read(document, type)!));
            after = page.Documents.Count > 0 ? page.Documents[^1].Id : after;
        }
        while (page.HasMore);

        return items;
    }
}
