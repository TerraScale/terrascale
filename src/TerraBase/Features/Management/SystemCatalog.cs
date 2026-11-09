using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal readonly union CatalogCheck(Done, Unavailable);

internal interface ICatalogGuard
{
    ValueTask<CatalogCheck> EnsureCatalogAsync(CancellationToken cancellationToken);

    ValueTask<CatalogCheck> CheckReadAsync(IStorageTransaction transaction, CancellationToken cancellationToken);
}

internal sealed class SystemCatalog(IStorageEngine engine, ICatalogGuard guard)
{
    private const long MaxPageBytes = 1024 * 1024;

    public static string DatabaseState(string databaseId) => $"databases/{databaseId}/";

    public static string ProjectState(string projectId) => $"projects/{projectId}/";

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

    public ValueTask<CatalogCheck> CheckAsync(CancellationToken cancellationToken) =>
        ReadAsync<CatalogCheck>(static _ => ValueTask.FromResult<CatalogCheck>(new Done()), static error => error, cancellationToken);

    public ValueTask<T> WriteAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        WriteAsync(write, unavailable, durable: false, file, line, cancellationToken);

    public ValueTask<T> WriteDurableAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        WriteAsync(write, unavailable, durable: true, file, line, cancellationToken);

    private async ValueTask<T> WriteAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, bool durable, string file, int line, CancellationToken cancellationToken) =>
        await guard.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false) switch
        {
            Done => await WriteUncheckedAsync(async transaction =>
                await guard.CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => await write(transaction).ConfigureAwait(false),
                    Unavailable refused => unavailable(refused),
                }, cancellationToken, durable, file, line).ConfigureAwait(false),
            Unavailable refused => unavailable(refused),
        };

    public async ValueTask<T> ReadUncheckedAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    public async ValueTask<T> WriteUncheckedAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken, bool durable = false,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = durable
            ? await store.BeginDurableTransactionAsync(cancellationToken, file, line).ConfigureAwait(false)
            : await store.BeginTransactionAsync(write: true, cancellationToken, file, line).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await write(transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    public ValueTask<ManagementPage<DatabasePurgeRecord>?> ListDatabasePurgesAsync(string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementPage<DatabasePurgeRecord>?>(async transaction =>
        {
            var page = await transaction.ListAsync(DatabasePurge.Kind, after, Math.Clamp(limit, 1, 100), MaxPageBytes, cancellationToken).ConfigureAwait(false);
            List<DatabasePurgeRecord> items = [];
            foreach (var stored in page.Records)
            {
                if (stored.Json.Span.TrimStart(" \t\r\n"u8) is [(byte)'{', ..])
                {
                    try
                    {
                        if (EntityPayload.Read(stored, TerraBaseJsonContext.Default.DatabasePurgeRecord) is { Database.DatabaseId: { } id } record && id == stored.Id)
                        {
                            items.Add(record);
                        }
                    }
                    catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
                    {
                    }
                }
            }

            return new ManagementPage<DatabasePurgeRecord>(items, page.HasMore && page.Records.Count > 0 ? page.Records[^1].Id : null);
        }, static _ => null, cancellationToken);

    public ValueTask<bool> AreActiveCopiesAbsentAsync(TenantContext database, CancellationToken cancellationToken) =>
        ReadAsync<bool>(async transaction =>
            await GetAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false) is { } record
            && DatabasePurge.SameDatabase(record.Database, database)
            && await DatabasePurge.AreActiveCopiesAbsentAsync(transaction, record, cancellationToken).ConfigureAwait(false), static _ => false, cancellationToken);

    public ValueTask<bool> MarkBackupVerifiedAsync(TenantContext database, DateTimeOffset verifiedAt, CancellationToken cancellationToken) =>
        WriteAsync<bool>(async transaction =>
        {
            var record = await GetAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
            if (record is null || !DatabasePurge.SameDatabase(record.Database, database)
                || !await DatabasePurge.AreActiveCopiesAbsentAsync(transaction, record, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (record.BackupVerifiedAt is null)
            {
                await PutAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, record with { BackupVerifiedAt = verifiedAt },
                    TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }, static _ => false, cancellationToken);

    public static async ValueTask<T?> GetAsync<T>(
        IStorageTransaction transaction, string kind, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        if (typeof(T) == typeof(ManagedDatabase) && await DatabasePurge.IsDeletedAsync(transaction, id, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var stored = await transaction.GetAsync(kind, id, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : EntityPayload.Read(stored, type);
    }

    public static async ValueTask PutAsync<T>(
        IStorageTransaction transaction, string kind, string id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await transaction.PutAsync(kind, id, EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(value, type), id, nested: true), cancellationToken).ConfigureAwait(false);

    public static async ValueTask<bool> AnyAsync(IStorageTransaction transaction, string kind, CancellationToken cancellationToken) =>
        (await transaction.ListAsync(kind, afterId: null, limit: 1, MaxPageBytes, cancellationToken).ConfigureAwait(false)).Records.Count > 0;

    public static async ValueTask<ManagementPage<T>> ListAsync<T>(
        IStorageTransaction transaction, string kind, string? after, int limit, JsonTypeInfo<T> type, Func<T, string> id, CancellationToken cancellationToken)
    {
        var page = await transaction.ListAsync(kind, after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        var items = page.Records.Select(stored => EntityPayload.Read(stored, type)!).ToList();
        return new ManagementPage<T>(items, page.HasMore ? id(items[^1]) : null);
    }

    public static async ValueTask<IReadOnlyList<T>> ListAllAsync<T>(
        IStorageTransaction transaction, string kind, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? after = null;
        RecordPage page;
        do
        {
            page = await transaction.ListAsync(kind, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Records.Select(stored => EntityPayload.Read(stored, type)!));
            after = page.Records.Count > 0 ? page.Records[^1].Id : after;
        }
        while (page.HasMore);

        return items;
    }
}
