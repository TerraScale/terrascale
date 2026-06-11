using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// TerraScale source facade for an EF Core DbContext.
/// </summary>
public sealed class TerraScaleEntitySource<TContext>
    where TContext : DbContext
{
    private readonly TContext context;
    private readonly TerraScaleClient client;
    private readonly TerraScaleEntityFrameworkCoreOptions options;

    /// <summary>
    /// Creates a TerraScale source facade for a DbContext.
    /// </summary>
    public TerraScaleEntitySource(
        TContext context,
        TerraScaleClient client,
        TerraScaleEntityFrameworkCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        this.context = context;
        this.client = client;
        this.options = options;
    }

    /// <summary>
    /// Creates a TerraScale entity set for the requested EF Core entity type.
    /// </summary>
    public TerraScaleEntitySet<TEntity> Set<TEntity>()
        where TEntity : class =>
        new(context, client, options);

    /// <summary>
    /// Persists tracked EF Core entity changes to TerraScale documents.
    /// </summary>
    public async Task<TerraScaleEntitySaveChangesResult> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.DetectChanges();

        var created = 0;
        var upserted = 0;
        var deleted = 0;
        var entries = context.ChangeTracker
            .Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        foreach (var entry in entries)
        {
            var mapping = TerraScaleEntityMapping.Create(context, entry.Metadata.ClrType, options);
            switch (entry.State)
            {
                case EntityState.Added:
                    await client.Documents.CreateAsync(
                        mapping.RepositoryName,
                        entry.Entity,
                        cancellationToken).ConfigureAwait(false);
                    entry.State = EntityState.Unchanged;
                    created++;
                    break;

                case EntityState.Modified:
                    await client.Documents.UpsertAsync(
                        mapping.RepositoryName,
                        mapping.GetDocumentId(entry.Entity),
                        entry.Entity,
                        cancellationToken).ConfigureAwait(false);
                    entry.State = EntityState.Unchanged;
                    upserted++;
                    break;

                case EntityState.Deleted:
                    await client.Documents.DeleteAsync(
                        mapping.RepositoryName,
                        mapping.GetDocumentId(entry.Entity),
                        cancellationToken).ConfigureAwait(false);
                    entry.State = EntityState.Detached;
                    deleted++;
                    break;
            }
        }

        return new TerraScaleEntitySaveChangesResult(created, upserted, deleted);
    }
}
