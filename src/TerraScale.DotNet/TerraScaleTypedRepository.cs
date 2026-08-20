using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Typed document operations for one TerraScale repository.
/// </summary>
/// <typeparam name="TEntity">Application model stored in the repository document data.</typeparam>
public sealed class TerraScaleTypedRepository<TEntity>
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;
    private readonly TerraScaleClient client;

    internal TerraScaleTypedRepository(TerraScaleClient client, string repository)
    {
        this.client = client;
        Repository = repository;
    }

    /// <summary>
    /// Repository id or name targeted by this typed facade.
    /// </summary>
    public string Repository { get; }

    /// <summary>
    /// Creates a typed document.
    /// </summary>
    public async Task<TerraScaleTypedDocument<TEntity>> CreateAsync(
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        var document = await client.Documents.CreateAsync(Repository, entity, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(document);
    }

    /// <summary>
    /// Gets a typed document by id.
    /// </summary>
    public async Task<TerraScaleTypedDocument<TEntity>> GetAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var document = await client.Documents.GetAsync(Repository, documentId, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(document);
    }

    /// <summary>
    /// Gets a typed document with explicit stale-read behavior.
    /// </summary>
    public async Task<TerraScaleTypedDocument<TEntity>> GetAsync(
        string documentId,
        TerraScaleDocumentReadOptions? options,
        CancellationToken cancellationToken = default)
    {
        var document = await client.Documents.GetAsync(Repository, documentId, options, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(document);
    }

    /// <summary>
    /// Upserts a typed document by id.
    /// </summary>
    public async Task<TerraScaleTypedDocument<TEntity>> UpsertAsync(
        string documentId,
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        var document = await client.Documents.UpsertAsync(Repository, documentId, entity, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(document);
    }

    /// <summary>
    /// Upserts a typed document and optionally requires the current change token.
    /// </summary>
    public async Task<TerraScaleTypedDocument<TEntity>> UpsertAsync(
        string documentId,
        TEntity entity,
        TerraScaleDocumentMutationOptions? options,
        CancellationToken cancellationToken = default)
    {
        var document = await client.Documents.UpsertAsync(
                Repository,
                documentId,
                entity,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        return Materialize(document);
    }

    /// <summary>
    /// Applies a partial patch and materializes the updated document as <typeparamref name="TEntity" />.
    /// </summary>
    public async Task<TerraScaleTypedDocument<TEntity>> PatchAsync<TPatch>(
        string documentId,
        TPatch patch,
        CancellationToken cancellationToken = default)
    {
        var document = await client.Documents.PatchAsync(Repository, documentId, patch, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(document);
    }

    /// <summary>
    /// Patches a typed document and optionally requires the current change token.
    /// </summary>
    public async Task<TerraScaleTypedDocument<TEntity>> PatchAsync<TPatch>(
        string documentId,
        TPatch patch,
        TerraScaleDocumentMutationOptions? options,
        CancellationToken cancellationToken = default)
    {
        var document = await client.Documents.PatchAsync(
                Repository,
                documentId,
                patch,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        return Materialize(document);
    }

    /// <summary>
    /// Runs typed create, upsert, patch, and delete document operations as one batch.
    /// </summary>
    public async Task<TerraScaleTypedDocumentBatch<TEntity>> BatchAsync(
        IReadOnlyList<TerraScaleTypedDocumentBatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var batch = await client.Documents.BatchAsync(
                Repository,
                operations.Select(static operation => operation.ToDocumentOperation()).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        return Materialize(batch);
    }

    /// <summary>
    /// Lists typed documents in this repository.
    /// </summary>
    public async Task<TerraScaleTypedDocumentPage<TEntity>> ListAsync(
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var page = await client.Documents.ListAsync(Repository, skip, take, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(page);
    }

    /// <summary>
    /// Runs a raw TQL query and materializes matching documents as <typeparamref name="TEntity" />.
    /// </summary>
    public async Task<TerraScaleTypedDocumentPage<TEntity>> QueryAsync(
        string query,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        var page = await client.Tql.QueryAsync(Repository, query, skip, take, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(page);
    }

    /// <summary>
    /// Runs a structured TQL query and materializes matching documents as <typeparamref name="TEntity" />.
    /// </summary>
    public async Task<TerraScaleTypedDocumentPage<TEntity>> QueryAsync(
        TerraScaleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await client.Tql.QueryAsync(Repository, query, cancellationToken)
            .ConfigureAwait(false);
        return Materialize(page);
    }

    /// <summary>
    /// Deletes a typed document by id.
    /// </summary>
    public Task<TerraScaleDeletedDocument> DeleteAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        client.Documents.DeleteAsync(Repository, documentId, cancellationToken);

    /// <summary>
    /// Deletes a typed document and optionally requires the current change token.
    /// </summary>
    public Task<TerraScaleDeletedDocument> DeleteAsync(
        string documentId,
        TerraScaleDocumentMutationOptions? options,
        CancellationToken cancellationToken = default) =>
        client.Documents.DeleteAsync(Repository, documentId, options, cancellationToken);

    private static TerraScaleTypedDocumentPage<TEntity> Materialize(TerraScaleDocumentPage page) =>
        new(
            page.RepositoryId,
            page.Repository,
            page.Skip,
            page.Take,
            page.Total,
            page.Items.Select(Materialize).ToArray(),
            page.Routing);

    private static TerraScaleTypedDocumentBatch<TEntity> Materialize(TerraScaleDocumentBatch batch) =>
        new(
            batch.RepositoryId,
            batch.Repository,
            batch.Results.Select(Materialize).ToArray(),
            batch.Routing);

    private static TerraScaleTypedDocumentBatchResult<TEntity> Materialize(TerraScaleDocumentBatchResult result) =>
        new(
            result.Index,
            result.Operation,
            result.Id,
            result.Document is null ? null : Materialize(result.Document),
            result.Deleted);

    private static TerraScaleTypedDocument<TEntity> Materialize(TerraScaleDocument document) =>
        new(
            document.RepositoryId,
            document.Repository,
            document.Id,
            ReadEntity(document),
            document.LastModified,
            document.ChangeToken,
            document.SessionConsistencyToken,
            document.Routing);

    private static TEntity ReadEntity(TerraScaleDocument document)
    {
        try
        {
            return document.Data.Deserialize<TEntity>(JsonOptions)
                   ?? throw new JsonException("Document data was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"TerraScale document '{document.Id}' in repository '{document.Repository}' could not be materialized as '{typeof(TEntity).Name}'.",
                exception);
        }
    }
}

/// <summary>
/// Typed document returned by TerraScale repository endpoints.
/// </summary>
public sealed record TerraScaleTypedDocument<TEntity>(
    string RepositoryId,
    string Repository,
    string Id,
    TEntity Data,
    DateTimeOffset? LastModified,
    string? ChangeToken,
    string? SessionConsistencyToken = null,
    TerraScaleRoutingObservation? Routing = null);

/// <summary>
/// Page of typed TerraScale repository documents.
/// </summary>
public sealed record TerraScaleTypedDocumentPage<TEntity>(
    string RepositoryId,
    string Repository,
    int Skip,
    int Take,
    long Total,
    IReadOnlyList<TerraScaleTypedDocument<TEntity>> Items,
    TerraScaleRoutingObservation? Routing = null);

/// <summary>
/// A typed create, upsert, patch, or delete operation in a document batch.
/// </summary>
public sealed record TerraScaleTypedDocumentBatchOperation(
    string Operation,
    string? DocumentId,
    object? Data)
{
    /// <summary>
    /// Creates a new typed document with a server-generated ULID.
    /// </summary>
    public static TerraScaleTypedDocumentBatchOperation Create<TEntity>(TEntity entity) =>
        new("create", DocumentId: null, entity);

    /// <summary>
    /// Upserts a typed document at a caller-provided ULID.
    /// </summary>
    public static TerraScaleTypedDocumentBatchOperation Upsert<TEntity>(
        string documentId,
        TEntity entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TerraScaleTypedDocumentBatchOperation("upsert", documentId.Trim(), entity);
    }

    /// <summary>
    /// Applies a partial top-level JSON object patch to an existing typed document.
    /// </summary>
    public static TerraScaleTypedDocumentBatchOperation Patch<TPatch>(
        string documentId,
        TPatch patch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TerraScaleTypedDocumentBatchOperation("patch", documentId.Trim(), patch);
    }

    /// <summary>
    /// Deletes an existing typed document.
    /// </summary>
    public static TerraScaleTypedDocumentBatchOperation Delete(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TerraScaleTypedDocumentBatchOperation("delete", documentId.Trim(), Data: null);
    }

    internal TerraScaleDocumentBatchOperation ToDocumentOperation() =>
        new(Operation, DocumentId, Data);
}

/// <summary>
/// Result of a typed TerraScale document batch operation.
/// </summary>
public sealed record TerraScaleTypedDocumentBatch<TEntity>(
    string RepositoryId,
    string Repository,
    IReadOnlyList<TerraScaleTypedDocumentBatchResult<TEntity>> Results,
    TerraScaleRoutingObservation? Routing = null);

/// <summary>
/// Result item for a single typed batch operation.
/// </summary>
public sealed record TerraScaleTypedDocumentBatchResult<TEntity>(
    int Index,
    string Operation,
    string Id,
    TerraScaleTypedDocument<TEntity>? Document,
    TerraScaleDeletedDocument? Deleted);
