using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Catalog;

/// <summary>A collection and its document count.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="DocumentCount">Documents currently stored.</param>
public sealed record CollectionInfo(string Name, long DocumentCount);

/// <summary>Database and collection administration (offline catalog, schema metadata). Not implemented yet.</summary>
public interface ICatalog
{
    /// <summary>Lists the collections of a database; empty when the database does not exist.</summary>
    ValueTask<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(TenantContext database, CancellationToken cancellationToken);
}
