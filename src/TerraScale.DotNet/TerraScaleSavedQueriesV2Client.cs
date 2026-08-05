namespace TerraScale.DotNet;

/// <summary>
/// Client for version 2 project saved-query endpoints.
/// </summary>
public sealed class TerraScaleSavedQueriesV2Client
{
    private readonly TerraScaleClient client;

    internal TerraScaleSavedQueriesV2Client(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists saved queries with opaque conditional revisions.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleSavedQueryV2>> ListAsync(
        string projectId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ValidatePagination(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleSavedQueryV2>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectSavedQueriesV2List(projectId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets a saved query with its opaque conditional revision.
    /// </summary>
    public Task<TerraScaleSavedQueryV2> GetAsync(
        string projectId,
        string savedQueryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(savedQueryId);
        return client.SendAsync<TerraScaleSavedQueryV2>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectSavedQueryV2Get(projectId, savedQueryId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Updates a saved query when its opaque revision is current.
    /// </summary>
    public Task<TerraScaleSavedQueryV2> UpdateAsync(
        string projectId,
        string savedQueryId,
        TerraScaleUpdateSavedQueryV2Request request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(savedQueryId);
        ValidateRequest(request);
        return client.SendAsync<TerraScaleSavedQueryV2>(
            HttpMethod.Put,
            TerraScaleApiRoutes.ProjectSavedQueryV2Update(projectId, savedQueryId),
            request,
            cancellationToken);
    }

    private static void ValidateRequest(TerraScaleUpdateSavedQueryV2Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Revision);
    }

    private static void ValidatePagination(int skip, int take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be greater than or equal to zero.");
        }

        if (take is < 0 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be between zero and 500.");
        }
    }
}

/// <summary>
/// Request body for conditionally updating a version 2 project saved query.
/// </summary>
public sealed record TerraScaleUpdateSavedQueryV2Request(
    string Name,
    string Query,
    string Revision,
    string? RepositoryId = null);

/// <summary>
/// A version 2 saved query with an opaque revision for conditional updates.
/// </summary>
public sealed record TerraScaleSavedQueryV2(
    string SavedQueryId,
    string ProjectId,
    string Name,
    string Query,
    string? RepositoryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Revision);
