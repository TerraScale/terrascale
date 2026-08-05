namespace TerraScale.DotNet;

/// <summary>
/// Client for project saved-query endpoints.
/// </summary>
public sealed class TerraScaleSavedQueriesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleSavedQueriesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists saved queries in a project.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleSavedQuery>> ListAsync(
        string projectId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleSavedQuery>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectSavedQueriesList(projectId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a saved query in a project.
    /// </summary>
    public Task<TerraScaleSavedQuery> CreateAsync(
        string projectId,
        TerraScaleSaveQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ValidateRequest(request);
        return client.SendAsync<TerraScaleSavedQuery>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectSavedQueriesCreate(projectId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Updates a saved query in a project.
    /// </summary>
    public Task<TerraScaleSavedQuery> UpdateAsync(
        string projectId,
        string savedQueryId,
        TerraScaleSaveQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(savedQueryId);
        ValidateRequest(request);
        return client.SendAsync<TerraScaleSavedQuery>(
            HttpMethod.Put,
            TerraScaleApiRoutes.ProjectSavedQueryUpdate(projectId, savedQueryId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Deletes a saved query from a project.
    /// </summary>
    public Task<TerraScaleDeletedSavedQuery> DeleteAsync(
        string projectId,
        string savedQueryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(savedQueryId);
        return client.SendAsync<TerraScaleDeletedSavedQuery>(
            HttpMethod.Delete,
            TerraScaleApiRoutes.ProjectSavedQueryDelete(projectId, savedQueryId),
            body: null,
            cancellationToken);
    }

    private static void ValidateRequest(TerraScaleSaveQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
    }
}

/// <summary>
/// Request body for creating or updating a project saved query.
/// </summary>
public sealed record TerraScaleSaveQueryRequest(
    string Name,
    string Query,
    string? RepositoryId = null);

/// <summary>
/// A saved query in a project.
/// </summary>
public sealed record TerraScaleSavedQuery(
    string SavedQueryId,
    string ProjectId,
    string Name,
    string Query,
    string? RepositoryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Result of deleting a project saved query.
/// </summary>
public sealed record TerraScaleDeletedSavedQuery(
    string SavedQueryId,
    string ProjectId,
    bool Deleted);
