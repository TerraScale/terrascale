using System.Text.Json;

namespace TerraBase.Integration;

/// <summary>Adapts the live engine to the document producer contract.</summary>
public sealed class EngineDocumentProducer : IDocumentProducer
{
    private readonly TerraBaseEngine engine;

    /// <summary>Creates a producer over an opened engine.</summary>
    /// <param name="engine">The runtime engine that owns storage and metrics.</param>
    public EngineDocumentProducer(TerraBaseEngine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <inheritdoc />
    public ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default) =>
        engine.Documents.UpsertAsync(key, body, precondition, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default) =>
        engine.Documents.DeleteAsync(key, precondition, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StorageBatchResult> ApplyBatchAsync(
        IReadOnlyList<StorageMutation> mutations,
        CancellationToken cancellationToken = default) =>
        engine.ApplyBatchAsync(mutations, cancellationToken);
}
