using System.Text.Json;

namespace TerraBase.Integration;

/// <summary>Writes documents through the engine's configured storage path.</summary>
public interface IDocumentProducer
{
    /// <summary>Creates or replaces one document.</summary>
    ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one document.</summary>
    ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Applies several document mutations atomically when supported by the provider.</summary>
    /// <exception cref="NotSupportedException">
    /// The configured provider does not expose transactional batches.
    /// </exception>
    ValueTask<StorageBatchResult> ApplyBatchAsync(
        IReadOnlyList<StorageMutation> mutations,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads documents through the engine's configured storage path.</summary>
public interface IDocumentConsumer
{
    /// <summary>Reads one document snapshot.</summary>
    ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>Scans one tenant collection in deterministic key order.</summary>
    IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default);
}
