using System.Text.Json;
using TerraScale.Auth.Contracts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Store;

/// <summary>A stored document and the opaque version (ETag) a conditional write must name.</summary>
/// <param name="Document">The document.</param>
/// <param name="Version">Its current version.</param>
public sealed record Versioned<T>(T Document, string Version);

/// <summary>
/// The conditional writes that counter and key-rotation state relies on, addressed by collection,
/// instance and key. Implementations must make <see cref="TryCreateAsync{T}"/> and
/// <see cref="TryReplaceAsync{T}"/> atomic at the storage authority, across every replica, and
/// <see cref="GetAsync{T}"/> must return the latest committed version from that authority.
/// </summary>
public interface IDocumentStore
{
    /// <summary>The document and its version, or <see langword="null"/> when it does not exist.</summary>
    Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken);

    /// <summary>Creates the document only if the address is free. Returns <see langword="false"/> when it is taken.</summary>
    Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the document only if its version is still <paramref name="version"/>.
    /// Returns <see langword="false"/> when another writer changed it.
    /// </summary>
    Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDocumentStore"/> on <see cref="AuthStore"/>: Documents v1 natural-key addresses,
/// create-only (<c>If-None-Match: *</c>) and replace-if-version (<c>If-Match</c>).
/// </summary>
public sealed class TerraBaseDocumentStore(AuthStore store) : IDocumentStore
{
    /// <inheritdoc />
    public async Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
        await store.GetAsync(collection, instanceId, key, cancellationToken).ConfigureAwait(false) is { } found
            ? new Versioned<T>(found.Document.Deserialize<T>() ?? throw new JsonException($"A {collection} document is empty."), found.Version)
            : null;

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken)
    {
        try
        {
            await store.CreateAsync(collection, instanceId, key, JsonSerializer.SerializeToElement(document), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken) =>
        store.CompareAndPutAsync(collection, instanceId, key, JsonSerializer.SerializeToElement(document), version, cancellationToken);
}

/// <summary>
/// Wraps a store so every failure other than caller cancellation surfaces as 503
/// <see cref="ApiException.Unavailable"/> (an inner 503 passes through; any other status,
/// <see cref="ApiException"/> included, is wrapped), with the original failure kept as its cause for
/// server logs. Consumers of shared state use it, so an outage, a missing collection or a
/// rejected credential fails closed instead of answering 500.
/// </summary>
public sealed class FailClosedDocumentStore(IDocumentStore inner) : IDocumentStore
{
    /// <inheritdoc />
    public Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
        Guard(() => inner.GetAsync<T>(collection, instanceId, key, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken) =>
        Guard(() => inner.TryCreateAsync(collection, instanceId, key, document, cancellationToken), cancellationToken);

    /// <inheritdoc />
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
