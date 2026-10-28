namespace TerraScale.DotNet.TerraBase;

/// <summary>An observed workload's recommended single-field index.</summary>
/// <param name="Id">Opaque collection:field selector; not a ULID.</param>
/// <param name="Collection">Collection name.</param>
/// <param name="Fields">Fields to index.</param>
/// <param name="Reason">Reason for recommendation.</param>
/// <param name="Queries">Queries using the field without an index.</param>
public sealed record TerraBaseIndexRecommendation(string Id, string Collection, IReadOnlyList<string> Fields, string Reason, long Queries);

/// <summary>Recommendations ordered by query count and id.</summary>
/// <param name="Items">Recommended indexes.</param>
public sealed record TerraBaseIndexRecommendationList(IReadOnlyList<TerraBaseIndexRecommendation> Items);

/// <summary>Result of explicitly applying a recommendation.</summary>
/// <param name="Status">applied or already-applied.</param>
/// <param name="Index">Created or existing index.</param>
public sealed record TerraBaseAppliedIndexRecommendation(string Status, TerraBaseIndex Index);

public sealed partial class TerraBaseDatabase
{
    /// <summary>Lists workload-based index recommendations. Requires databases:read.</summary>
    public async Task<TerraBaseIndexRecommendationList> ListIndexRecommendationsAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/index-recommendations", null,
            Json.TerraBaseIndexRecommendationList, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Applies an opaque recommendation id. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseAppliedIndexRecommendation> ApplyIndexRecommendationAsync(string recommendationId, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/index-recommendations/" + TerraBaseConnection.Segment(recommendationId, nameof(recommendationId)) + "/apply",
            null, Json.TerraBaseAppliedIndexRecommendation, null, cancellationToken).ConfigureAwait(false)).Value;
}
