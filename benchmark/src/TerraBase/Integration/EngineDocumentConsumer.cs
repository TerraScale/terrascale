namespace TerraBase.Integration;

/// <summary>Adapts the live engine to the document consumer contract.</summary>
public sealed class EngineDocumentConsumer : IDocumentConsumer
{
    private readonly IDocumentStore store;

    /// <summary>Creates a consumer over an engine document store.</summary>
    /// <param name="store">The document store owned by the runtime engine.</param>
    public EngineDocumentConsumer(IDocumentStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default) =>
        store.ReadAsync(key, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default) =>
        store.ScanAsync(tenant, collection, cancellationToken);
}
