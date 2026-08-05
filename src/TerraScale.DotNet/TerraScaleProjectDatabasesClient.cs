namespace TerraScale.DotNet;

/// <summary>
/// Client for project-scoped TerraScale database endpoints.
/// </summary>
public sealed class TerraScaleProjectDatabasesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleProjectDatabasesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Creates a logical database in a project.
    /// </summary>
    public Task<TerraScaleProjectDatabase> CreateAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return client.SendAsync<TerraScaleProjectDatabase>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectDatabasesCreate(projectId),
            new TerraScaleCreateProjectDatabaseRequest(name.Trim()),
            cancellationToken);
    }

    /// <summary>
    /// Lists logical databases in a project.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleProjectDatabase>> ListAsync(
        string projectId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleProjectDatabase>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectDatabasesList(projectId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets a logical project database by id.
    /// </summary>
    public Task<TerraScaleProjectDatabase> GetAsync(
        string projectId,
        string databaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return client.SendAsync<TerraScaleProjectDatabase>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectDatabaseGet(projectId, databaseId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Deletes a logical project database after explicit acknowledgement of its id.
    /// </summary>
    public Task<TerraScaleDeletedProjectDatabase> DeleteAsync(
        string projectId,
        string databaseId,
        TerraScaleDeleteDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(databaseId.Trim(), options.Confirmation, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The deletion confirmation must exactly match the target database id.",
                nameof(options));
        }

        return client.SendAsync<TerraScaleDeletedProjectDatabase>(
            HttpMethod.Delete,
            TerraScaleApiRoutes.ProjectDatabaseDelete(projectId, databaseId),
            body: null,
            request => request.Headers.TryAddWithoutValidation(
                "X-TerraScale-Delete-Confirmation",
                options.Confirmation),
            cancellationToken);
    }
}

/// <summary>
/// Request body for creating a project-scoped logical database.
/// </summary>
public sealed record TerraScaleCreateProjectDatabaseRequest(
    string Name);

/// <summary>
/// Project-scoped logical database metadata.
/// </summary>
public sealed record TerraScaleProjectDatabase(
    string DatabaseId,
    string ProjectId,
    string Name,
    IReadOnlyList<string> Regions,
    DateTimeOffset CreatedAt,
    long Version);

/// <summary>
/// Result of deleting a project-scoped logical database.
/// </summary>
public sealed record TerraScaleDeletedProjectDatabase(
    string DatabaseId,
    string ProjectId,
    string Name,
    bool Deleted,
    int EnvironmentsDeleted,
    int PhysicalDatabasesDeleted,
    int RepositoriesDeleted);
