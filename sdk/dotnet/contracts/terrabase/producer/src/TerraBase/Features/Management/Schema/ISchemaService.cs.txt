using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Schema;

/// <summary>The collections of a database.</summary>
/// <param name="Items">Every collection, in name order.</param>
public sealed record CollectionList(IReadOnlyList<CollectionStats> Items);

/// <summary>The fields found in a sample of a collection's documents.</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Documents">Live documents in the collection.</param>
/// <param name="SampledDocuments">Documents read, the first ones in id order.</param>
/// <param name="Fields">Fields in path order.</param>
/// <param name="UniqueConstraints">Declared single and compound unique keys.</param>
public sealed record CollectionSchema(string Collection, long Documents, int SampledDocuments, IReadOnlyList<SchemaField> Fields, IReadOnlyList<UniqueConstraint>? UniqueConstraints = null);

/// <summary>One field seen in the sample.</summary>
/// <param name="Path">Dotted path of object member names.</param>
/// <param name="Present">Sampled documents that contain the path.</param>
/// <param name="Types">How many of those hold each JSON type: <c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, <c>null</c>, <c>object</c> or <c>array</c>.</param>
public sealed record SchemaField(string Path, int Present, IReadOnlyDictionary<string, int> Types);

/// <summary>The collections of a database, or that listing them exhausted the engine read budget.</summary>
public readonly union ListCollectionsResult(CollectionList, Validation);

/// <summary>The inferred fields of a collection, that it does not exist, or that inferring them exceeded a budget.</summary>
public readonly union InferSchemaResult(CollectionSchema, NotFound, Validation);

/// <summary>Collection counts, or why storage cannot be counted.</summary>
public readonly union CountCollectionsResult(IReadOnlyList<CollectionStats>, NotFound, Validation);

/// <summary>Collection views with inferred fields and declared unique keys.</summary>
public interface ISchemaService
{
    /// <summary>Lists the collections of <paramref name="database"/>; empty when its storage is missing.</summary>
    ValueTask<ListCollectionsResult> ListCollectionsAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the live documents of each collection and their JSON bytes without reading their bodies; <see cref="NotFound"/>
    /// when the storage is missing, also when the database was deleted while the count started.
    /// </summary>
    ValueTask<CountCollectionsResult> CountCollectionsAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Infers the fields of <paramref name="collection"/>.</summary>
    ValueTask<InferSchemaResult> InferAsync(TenantContext database, string collection, CancellationToken cancellationToken);
}
