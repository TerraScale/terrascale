using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>EF Core source facade over TerraScale documents.</summary>
public sealed class TerraScaleEntitySource<TContext> where TContext : DbContext
{
    private readonly TContext context; private readonly TerraScaleClient client; private readonly TerraScaleEntityFrameworkCoreOptions options;
    /// <summary>Creates a source over the supplied DbContext and client.</summary>
    public TerraScaleEntitySource(TContext context, TerraScaleClient client, TerraScaleEntityFrameworkCoreOptions options) { this.context = context ?? throw new ArgumentNullException(nameof(context)); this.client = client ?? throw new ArgumentNullException(nameof(client)); this.options = options ?? throw new ArgumentNullException(nameof(options)); }
    /// <summary>Gets the mapped set for an entity.</summary>
    public TerraScaleEntitySet<TEntity> Set<TEntity>() where TEntity : class => new(context, client, options);
    /// <summary>Persists tracked additions, modifications, and deletions asynchronously.</summary>
    public async Task<TerraScaleEntitySaveChangesResult> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.DetectChanges(); var created = 0; var upserted = 0; var deleted = 0;
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
        {
            var mapping = TerraScaleEntityMapping.Create(context, entry.Metadata.ClrType, options);
            switch (entry.State)
            {
                case EntityState.Added: await client.Documents.UpsertAsync(mapping.DatabaseName, mapping.CollectionName, mapping.GetDocumentId(entry.Entity), entry.Entity, cancellationToken).ConfigureAwait(false); entry.State = EntityState.Unchanged; created++; break;
                case EntityState.Modified: await client.Documents.UpsertAsync(mapping.DatabaseName, mapping.CollectionName, mapping.GetDocumentId(entry.Entity), entry.Entity, cancellationToken).ConfigureAwait(false); entry.State = EntityState.Unchanged; upserted++; break;
                case EntityState.Deleted: await client.Documents.DeleteAsync(mapping.DatabaseName, mapping.CollectionName, mapping.GetDocumentId(entry.Entity), cancellationToken).ConfigureAwait(false); entry.State = EntityState.Detached; deleted++; break;
            }
        }
        return new TerraScaleEntitySaveChangesResult(created, upserted, deleted);
    }
}
