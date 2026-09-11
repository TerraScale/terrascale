using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>Async document operations for one EF Core entity type.</summary>
public sealed class TerraScaleEntitySet<TEntity> where TEntity : class
{
    private readonly TerraScaleClient client;
    private readonly TerraScaleEntityMapping mapping;
    private readonly JsonSerializerOptions jsonOptions;
    /// <summary>Creates a set over the supplied DbContext model and client.</summary>
    public TerraScaleEntitySet(DbContext context, TerraScaleClient client, TerraScaleEntityFrameworkCoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(client);
        var resolved = options ?? TerraScaleDbContextExtensions.GetTerraScaleOptions(context);
        mapping = TerraScaleEntityMapping.Create(context, typeof(TEntity), resolved); this.client = client; jsonOptions = resolved.JsonSerializerOptions;
    }
    /// <summary>Mapped database name.</summary><value></value>
    public string DatabaseName => mapping.DatabaseName;
    /// <summary>Mapped collection name.</summary>
    public string CollectionName => mapping.CollectionName;
    /// <summary>Creates a document using the entity key.</summary>
    public Task<TerraScaleDocument> AddAsync(TEntity entity, CancellationToken cancellationToken = default) => UpsertAsync(entity, cancellationToken);
    /// <summary>Upserts an entity.</summary>
    public Task<TerraScaleDocument> UpsertAsync(TEntity entity, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(entity); return client.Documents.UpsertAsync(DatabaseName, CollectionName, mapping.GetDocumentId(entity), entity, cancellationToken); }
    /// <summary>Gets an entity by key.</summary>
    public async Task<TEntity> GetAsync(object key, CancellationToken cancellationToken = default) => Materialize(await client.Documents.GetAsync(DatabaseName, CollectionName, mapping.FormatDocumentId(key), cancellationToken).ConfigureAwait(false));
    /// <summary>Gets an entity by key, returning null when absent.</summary>
    public async Task<TEntity?> FindAsync(object key, CancellationToken cancellationToken = default)
    { try { return await GetAsync(key, cancellationToken).ConfigureAwait(false); } catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; } }
    /// <summary>Lists entities from the collection.</summary>
    public async Task<IReadOnlyList<TEntity>> ListAsync(int take = 100, string? after = null, CancellationToken cancellationToken = default)
    { var page = await client.Documents.ListAsync(DatabaseName, CollectionName, take, after, cancellationToken).ConfigureAwait(false); return page.Items.Select(Materialize).ToArray(); }
    /// <summary>Deletes an entity by key.</summary>
    public Task DeleteAsync(object key, CancellationToken cancellationToken = default) => client.Documents.DeleteAsync(DatabaseName, CollectionName, mapping.FormatDocumentId(key), cancellationToken);
    private TEntity Materialize(TerraScaleDocument document) => document.Data.Deserialize<TEntity>(jsonOptions) ?? throw new JsonException($"Document '{document.Id}' could not be materialized as '{typeof(TEntity).Name}'.");
}
