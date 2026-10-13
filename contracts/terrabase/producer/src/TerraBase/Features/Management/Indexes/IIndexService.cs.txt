using TerraBase.Engine;
using TerraBase.Errors;

using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Indexes;

/// <summary>A secondary index of a managed database.</summary>
/// <param name="Name">Index name, unique per database.</param>
/// <param name="Collection">Indexed collection.</param>
/// <param name="Fields">Key fields in order.</param>
/// <param name="State">Always <c>active</c>: indexes are built when created.</param>
public sealed record IndexBody(string Name, string Collection, IReadOnlyList<string> Fields, string State)
{
    internal static IndexBody From(IndexDefinition index) => new(index.Name, index.Collection, [.. index.Fields.Select(field => field.Path)], "active");
}

/// <summary>The indexes of a database.</summary>
/// <param name="Items">Indexes in name order.</param>
public sealed record IndexListBody(IReadOnlyList<IndexBody> Items);

/// <summary>An index to create.</summary>
/// <param name="Collection">Collection to index.</param>
/// <param name="Fields">1 to 4 key fields.</param>
/// <param name="Name">Optional name; derived from the collection and fields when omitted.</param>
public sealed record CreateIndexRequest(string? Collection, IReadOnlyList<string>? Fields, string? Name);

/// <summary>A single-field index the observed queries would benefit from.</summary>
/// <param name="Id"><c>collection:field</c>.</param>
/// <param name="Collection">Collection.</param>
/// <param name="Fields">The field to index.</param>
/// <param name="Reason">Why it is recommended.</param>
/// <param name="Queries">Queries that filtered or sorted on the field without an index.</param>
public sealed record IndexRecommendationBody(string Id, string Collection, IReadOnlyList<string> Fields, string Reason, long Queries);

/// <summary>The index recommendations of a database.</summary>
/// <param name="Items">Most queried first, then by id.</param>
public sealed record IndexRecommendationListBody(IReadOnlyList<IndexRecommendationBody> Items);

/// <summary>The outcome of applying a recommendation.</summary>
/// <param name="Status"><c>applied</c> when the index was created, <c>already-applied</c> when one already leads with the field.</param>
/// <param name="Index">The created or existing index.</param>
public sealed record AppliedIndexRecommendation(string Status, IndexBody Index);

/// <summary>The created index, or why it was not created.</summary>
public readonly union CreateIndexResult(IndexBody, NotFound, Conflict);

/// <summary>A dropped index, or that it (or its database's storage) does not exist.</summary>
public readonly union DropIndexResult(Done, NotFound);

/// <summary>The applied or already existing index, or why the recommendation was not applied.</summary>
public readonly union ApplyIndexRecommendationResult(AppliedIndexRecommendation, NotFound, Conflict, Unavailable);

/// <summary>The recommendations for a database, or why they cannot be read now.</summary>
public readonly union RecommendIndexesResult(IndexRecommendationListBody, Unavailable);

/// <summary>Index failures with their stable codes.</summary>
internal static class IndexErrors
{
    /// <summary>An index has the name, or indexes the same fields of the collection.</summary>
    public static readonly Conflict Exists = new("an index with this name, or on these fields of the collection, already exists", "index_exists");

    /// <summary>Another index has the name a recommendation's index would get.</summary>
    public static readonly Conflict DefaultNameTaken = new("another index already has the default name", "name_taken");
}

/// <summary>Secondary indexes of managed databases and recommendations from their query workload.</summary>
public interface IIndexService
{
    /// <summary>Reads a collection's automatic index policy; enabled unless explicitly opted out.</summary>
    ValueTask<GetAutoIndexPolicyResult> GetAutoIndexPolicyAsync(string userId, TenantContext database, string collection, CancellationToken cancellationToken);

    /// <summary>Sets a collection's automatic index policy, rechecking membership and write permission atomically.</summary>
    ValueTask<SetAutoIndexPolicyResult> SetAutoIndexPolicyAsync(string userId, TenantContext database, string collection, bool enabled, CancellationToken cancellationToken);

    /// <summary>Lists the indexes of a database, optionally of one collection.</summary>
    ValueTask<IndexListBody> ListAsync(TenantContext database, string? collection, CancellationToken cancellationToken);

    /// <summary>Builds an index; <see cref="IndexErrors.Exists"/> when the name or the same collection and fields exist.</summary>
    ValueTask<CreateIndexResult> CreateAsync(TenantContext database, IndexDefinition index, CancellationToken cancellationToken);

    /// <summary>Drops an index.</summary>
    ValueTask<DropIndexResult> DropAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Recommends single-field indexes for fields queries used without one.</summary>
    ValueTask<RecommendIndexesResult> RecommendAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Creates the index a recommendation describes; not found for an unknown recommendation.</summary>
    ValueTask<ApplyIndexRecommendationResult> ApplyAsync(TenantContext database, string recommendationId, CancellationToken cancellationToken);
}
