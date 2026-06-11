using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// Async TerraScale document source for one EF Core entity type.
/// </summary>
public sealed class TerraScaleEntitySet<TEntity>
    where TEntity : class
{
    private readonly TerraScaleClient client;
    private readonly TerraScaleEntityMapping mapping;
    private readonly JsonSerializerOptions jsonOptions;

    /// <summary>
    /// Creates a TerraScale entity set backed by an EF Core model and TerraScale client.
    /// </summary>
    public TerraScaleEntitySet(
        DbContext context,
        TerraScaleClient client,
        TerraScaleEntityFrameworkCoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(client);

        this.client = client;
        var resolvedOptions = options ?? TerraScaleDbContextExtensions.GetTerraScaleOptions(context);
        mapping = TerraScaleEntityMapping.Create(context, typeof(TEntity), resolvedOptions);
        jsonOptions = resolvedOptions.JsonSerializerOptions;
    }

    /// <summary>
    /// TerraScale repository name resolved from EF Core metadata.
    /// </summary>
    public string RepositoryName => mapping.RepositoryName;

    /// <summary>
    /// Creates a new TerraScale document for the entity.
    /// </summary>
    public Task<TerraScaleDocument> AddAsync(
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return client.Documents.CreateAsync(RepositoryName, entity, cancellationToken);
    }

    /// <summary>
    /// Upserts the entity using its EF Core primary key as the TerraScale document id.
    /// </summary>
    public Task<TerraScaleDocument> UpsertAsync(
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return client.Documents.UpsertAsync(
            RepositoryName,
            mapping.GetDocumentId(entity),
            entity,
            cancellationToken);
    }

    /// <summary>
    /// Gets an entity by primary key and throws if the TerraScale document does not exist.
    /// </summary>
    public async Task<TEntity> GetAsync(
        object key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var document = await client.Documents.GetAsync(
            RepositoryName,
            mapping.FormatDocumentId(key),
            cancellationToken).ConfigureAwait(false);

        return Materialize(document);
    }

    /// <summary>
    /// Gets an entity by primary key or returns <see langword="null"/> when TerraScale returns HTTP 404.
    /// </summary>
    public async Task<TEntity?> FindAsync(
        object key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists entities from the mapped TerraScale repository.
    /// </summary>
    public async Task<IReadOnlyList<TEntity>> ListAsync(
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(skip, take);

        var page = await client.Documents.ListAsync(
            RepositoryName,
            skip,
            take,
            cancellationToken).ConfigureAwait(false);

        return page.Items.Select(Materialize).ToArray();
    }

    /// <summary>
    /// Runs a TerraScale Query Language query and materializes matching entities.
    /// </summary>
    public async Task<IReadOnlyList<TEntity>> QueryAsync(
        string tql,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tql);

        var page = await client.Tql.QueryAsync(
            RepositoryName,
            tql,
            skip,
            take,
            cancellationToken).ConfigureAwait(false);

        return page.Items.Select(Materialize).ToArray();
    }

    /// <summary>
    /// Deletes an entity document by primary key.
    /// </summary>
    public Task<TerraScaleDeletedDocument> DeleteAsync(
        object key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return client.Documents.DeleteAsync(
            RepositoryName,
            mapping.FormatDocumentId(key),
            cancellationToken);
    }

    private TEntity Materialize(TerraScaleDocument document)
    {
        var entity = document.Data.Deserialize<TEntity>(jsonOptions);
        return entity ?? throw new JsonException(
            $"TerraScale document '{document.Id}' in repository '{document.Repository}' could not be materialized as '{typeof(TEntity).Name}'.");
    }

    private static void ValidatePage(int skip, int take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be greater than or equal to zero.");
        }

        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be greater than zero.");
        }
    }
}
