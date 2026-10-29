using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A collection's live document count and JSON size, distinct from stored creation metadata.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="Documents">Live documents.</param>
/// <param name="Bytes">Their JSON bytes.</param>
public sealed record TerraBaseCollectionStats(string Name, long Documents, long Bytes);

/// <summary>All collection statistics in name order.</summary>
/// <param name="Items">Collections with counts and sizes.</param>
public sealed record TerraBaseCollectionList(IReadOnlyList<TerraBaseCollectionStats> Items) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

public sealed partial class TerraBaseDatabase
{
    /// <summary>Reads all collection counts/sizes via GET /schema; requires databases:read. An expensive count fails 400.</summary>
    public async Task<TerraBaseCollectionList> ListCollectionStatsAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/schema", null, Json.TerraBaseCollectionList, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads sampled fields and uniqueness declarations via GET /schema/{collection}; requires databases:read.</summary>
    public async Task<TerraBaseCollectionSchema> GetInferredSchemaAsync(string collection, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, UniqueSchemaPath(collection), null, Json.TerraBaseCollectionSchema, null, cancellationToken).ConfigureAwait(false)).Value;
}
