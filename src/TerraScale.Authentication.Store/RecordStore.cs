using System.Text.Json;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Store;

public sealed record Versioned<T>(T Body, string Version);

public interface IRecordStore
{
    Task<Versioned<T>?> GetAsync<T>(string table, string instanceId, string key, CancellationToken cancellationToken);

    Task<bool> TryCreateAsync<T>(string table, string instanceId, string key, T rowData, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync<T>(string table, string instanceId, string key, T rowData, string version, CancellationToken cancellationToken);
}

public sealed class TerraBaseRecordStore(AuthStore store) : IRecordStore
{
    public async Task<Versioned<T>?> GetAsync<T>(string table, string instanceId, string key, CancellationToken cancellationToken) =>
        await store.GetAsync(table, instanceId, key, cancellationToken).ConfigureAwait(false) is { } found
            ? new Versioned<T>(found.Body.Deserialize<T>() ?? throw new JsonException($"A {table} record is empty."), found.Version)
            : null;

    public async Task<bool> TryCreateAsync<T>(string table, string instanceId, string key, T rowData, CancellationToken cancellationToken)
    {
        try
        {
            await store.CreateAsync(table, instanceId, key, JsonSerializer.SerializeToElement(rowData), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException)
        {
            return false;
        }
    }

    public Task<bool> TryReplaceAsync<T>(string table, string instanceId, string key, T rowData, string version, CancellationToken cancellationToken) =>
        store.CompareAndPutAsync(table, instanceId, key, JsonSerializer.SerializeToElement(rowData), version, cancellationToken);
}

public sealed class FailClosedRecordStore(IRecordStore inner) : IRecordStore
{
    public Task<Versioned<T>?> GetAsync<T>(string table, string instanceId, string key, CancellationToken cancellationToken) =>
        Guard(() => inner.GetAsync<T>(table, instanceId, key, cancellationToken), cancellationToken);

    public Task<bool> TryCreateAsync<T>(string table, string instanceId, string key, T rowData, CancellationToken cancellationToken) =>
        Guard(() => inner.TryCreateAsync(table, instanceId, key, rowData, cancellationToken), cancellationToken);

    public Task<bool> TryReplaceAsync<T>(string table, string instanceId, string key, T rowData, string version, CancellationToken cancellationToken) =>
        Guard(() => inner.TryReplaceAsync(table, instanceId, key, rowData, version, cancellationToken), cancellationToken);

    private static async Task<TResult> Guard<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not (ApiException { Status: 503 } or OutOfMemoryException)
            && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw ApiException.Unavailable(failure);
        }
    }
}
