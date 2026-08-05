namespace TerraScale.DotNet;

/// <summary>
/// Client for project database environment endpoints.
/// </summary>
public sealed class TerraScaleEnvironmentsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleEnvironmentsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Creates an environment under a project database.
    /// </summary>
    public Task<TerraScaleProjectDatabaseEnvironment> CreateAsync(
        string projectId,
        string databaseId,
        string name,
        string kind = "Custom",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return client.SendAsync<TerraScaleProjectDatabaseEnvironment>(
            HttpMethod.Post,
            TerraScaleApiRoutes.EnvironmentsCreate(projectId, databaseId),
            new TerraScaleCreateProjectDatabaseEnvironmentRequest(name.Trim(), kind.Trim()),
            cancellationToken);
    }

    /// <summary>
    /// Lists environments under a project database.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleProjectDatabaseEnvironment>> ListAsync(
        string projectId,
        string databaseId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleProjectDatabaseEnvironment>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.EnvironmentsList(projectId, databaseId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets an environment under a project database.
    /// </summary>
    public Task<TerraScaleProjectDatabaseEnvironment> GetAsync(
        string projectId,
        string databaseId,
        string environmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        return client.SendAsync<TerraScaleProjectDatabaseEnvironment>(
            HttpMethod.Get,
            TerraScaleApiRoutes.EnvironmentGet(projectId, databaseId, environmentId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Requests deletion of an environment under a project database.
    /// A deletion is complete only when <see cref="TerraScaleDeletedProjectDatabaseEnvironment.Deleted"/>
    /// is <see langword="true"/> and <see cref="TerraScaleDeletedProjectDatabaseEnvironment.Status"/>
    /// is <c>Completed</c>.
    /// </summary>
    public Task<TerraScaleDeletedProjectDatabaseEnvironment> DeleteAsync(
        string projectId,
        string databaseId,
        string environmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        return client.SendAsync<TerraScaleDeletedProjectDatabaseEnvironment>(
            HttpMethod.Delete,
            TerraScaleApiRoutes.EnvironmentDelete(projectId, databaseId, environmentId),
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// Request body for creating a project database environment.
/// </summary>
public sealed record TerraScaleCreateProjectDatabaseEnvironmentRequest(
    string Name,
    string Kind = "Custom");

/// <summary>
/// Project database environment metadata.
/// </summary>
public sealed record TerraScaleProjectDatabaseEnvironment(
    string EnvironmentId,
    string ProjectId,
    string DatabaseId,
    string Name,
    string Kind,
    string Status,
    DateTimeOffset CreatedAt,
    long Version);

/// <summary>
/// Result of deleting a project database environment.
/// </summary>
public sealed record TerraScaleDeletedProjectDatabaseEnvironment(
    string EnvironmentId,
    string ProjectId,
    string DatabaseId,
    string Name,
    bool Deleted,
    bool PhysicalDatabaseDeleted,
    int RepositoriesDeleted)
{
    /// <summary>
    /// The durable deletion operation identifier, when returned by the server.
    /// </summary>
    public string? OperationId { get; init; }

    /// <summary>
    /// The durable deletion status, when returned by the server. A missing value is not completion.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// The next scheduled retry time when deletion is pending, if returned by the server.
    /// </summary>
    public DateTimeOffset? RetryAt { get; init; }
}
