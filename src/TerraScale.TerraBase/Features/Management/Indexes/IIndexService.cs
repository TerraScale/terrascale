using TerraScale.TerraBase.Engine;

using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management.Indexes;

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

/// <summary>Secondary indexes of managed databases and recommendations from their query workload.</summary>
public interface IIndexService
{
    /// <summary>Lists the indexes of a database, optionally of one collection.</summary>
    ValueTask<IndexListBody> ListAsync(TenantContext database, string? collection, CancellationToken cancellationToken);

    /// <summary>Builds an index; <see cref="ManagementError.NameTaken"/> when the name or the same collection and fields exist.</summary>
    ValueTask<ManagementResult<IndexBody>> CreateAsync(TenantContext database, IndexDefinition index, CancellationToken cancellationToken);

    /// <summary>Drops an index; <see langword="false"/> when it does not exist.</summary>
    ValueTask<bool> DropAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Recommends single-field indexes for fields queries used without one.</summary>
    ValueTask<IndexRecommendationListBody> RecommendAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Creates the index a recommendation describes; <see cref="ManagementError.NotFound"/> for an unknown recommendation.</summary>
    ValueTask<ManagementResult<AppliedIndexRecommendation>> ApplyAsync(TenantContext database, string recommendationId, CancellationToken cancellationToken);
}
