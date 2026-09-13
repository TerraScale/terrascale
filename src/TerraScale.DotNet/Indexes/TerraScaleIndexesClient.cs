using System.Globalization;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Public collection index administration.</summary>
public sealed class TerraScaleIndexesClient(TerraScaleClient client)
{
    public Task<IndexListResponse> ListAsync(string database, string collection, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, Route(database, collection), TerraScaleContractJsonContext.Default.IndexListResponse, cancellationToken);

    public Task<IndexCreatedResponse> CreateAsync(string database, string collection, CreateIndexRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return client.SendNonRetryableJsonAsync(HttpMethod.Post, Route(database, collection), request,
            TerraScaleContractJsonContext.Default.CreateIndexRequest, TerraScaleContractJsonContext.Default.IndexCreatedResponse, cancellationToken);
    }

    public Task DeleteAsync(string database, string collection, string field, CancellationToken cancellationToken = default) =>
        client.SendNonRetryableNoContentAsync(HttpMethod.Delete, Route(database, collection) + "/" + TerraScaleManagementPaths.Escape(field), cancellationToken);

    public Task<RecommendationList> RecommendationsAsync(string database, string collection, int limit = 64, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        return client.SendJsonAsync(HttpMethod.Get, Route(database, collection) + "/recommendations?limit=" + limit.ToString(CultureInfo.InvariantCulture),
            TerraScaleContractJsonContext.Default.RecommendationList, cancellationToken);
    }

    private static string Route(string database, string collection) => TerraScaleManagementPaths.Collection(database, collection) + "/indexes";
}
