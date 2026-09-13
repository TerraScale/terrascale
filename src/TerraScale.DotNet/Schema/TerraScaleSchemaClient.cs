using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Durable collection schemas and conditional schema application.</summary>
public sealed class TerraScaleSchemaClient(TerraScaleClient client)
{
    public Task<SchemaDocument> GetAsync(string database, string collection, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, Route(database, collection), TerraScaleContractJsonContext.Default.SchemaDocument, cancellationToken);

    public Task<SchemaDocument> PutAsync(string database, string collection, CollectionSchema schema, string idempotencyKey, ulong? expectedGeneration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return client.PutCollectionSchemaAsync(Route(database, collection), schema, idempotencyKey, expectedGeneration, cancellationToken);
    }

    private static string Route(string database, string collection) => TerraScaleManagementPaths.Collection(database, collection) + "/schema";
}
