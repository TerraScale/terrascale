using System.Text.Json;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Store;

public sealed record Versioned<T>(T Document, string Version);

public interface IDocumentStore
{
    Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken);

    Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken);
}

public sealed class TerraBaseDocumentStore(AuthStore store) : IDocumentStore
{
    public async Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
        await store.GetAsync(collection, instanceId, key, cancellationToken).ConfigureAwait(false) is { } found
            ? new Versioned<T>(found.Document.Deserialize<T>() ?? throw new JsonException($"A {collection} record is empty."), found.Version)
            : null;

    public async Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken)
    {
        try
        {
            await store.CreateAsync(collection, instanceId, key, JsonSerializer.SerializeToElement(document), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorePreconditionFailedException)
        {
            return false;
        }
    }

    public Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken) =>
        store.CompareAndPutAsync(collection, instanceId, key, JsonSerializer.SerializeToElement(document), version, cancellationToken);
}

public sealed class FailClosedDocumentStore(IDocumentStore inner) : IDocumentStore
{
    public Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
        Guard(() => inner.GetAsync<T>(collection, instanceId, key, cancellationToken), cancellationToken);

    public Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken) =>
        Guard(() => inner.TryCreateAsync(collection, instanceId, key, document, cancellationToken), cancellationToken);

    public Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken) =>
        Guard(() => inner.TryReplaceAsync(collection, instanceId, key, document, version, cancellationToken), cancellationToken);

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
