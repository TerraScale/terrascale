namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale project endpoints.
/// </summary>
public sealed class TerraScaleProjectsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleProjectsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists projects in an organization that are visible to the current credentials.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleProject>> ListAsync(
        string organizationId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleProject>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectsList(organizationId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a project in an organization.
    /// </summary>
    public Task<TerraScaleProject> CreateAsync(
        string organizationId,
        TerraScaleCreateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleProject>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectsCreate(organizationId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Creates a project in an organization.
    /// </summary>
    public Task<TerraScaleProject> CreateAsync(
        string organizationId,
        string name,
        IReadOnlyList<string>? defaultPermissionKeys = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return CreateAsync(
            organizationId,
            new TerraScaleCreateProjectRequest(name.Trim(), defaultPermissionKeys),
            cancellationToken);
    }

    /// <summary>
    /// Gets a project by id.
    /// </summary>
    public Task<TerraScaleProject> GetAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return client.SendAsync<TerraScaleProject>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectGet(projectId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Updates a project.
    /// </summary>
    public Task<TerraScaleProject> UpdateAsync(
        string projectId,
        TerraScaleUpdateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleProject>(
            HttpMethod.Patch,
            TerraScaleApiRoutes.ProjectUpdate(projectId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Updates a project's display name.
    /// </summary>
    public Task<TerraScaleProject> UpdateAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return UpdateAsync(
            projectId,
            new TerraScaleUpdateProjectRequest(name.Trim()),
            cancellationToken);
    }
}

/// <summary>
/// Request body for creating a project.
/// </summary>
public sealed record TerraScaleCreateProjectRequest(
    string Name,
    IReadOnlyList<string>? DefaultPermissionKeys = null);

/// <summary>
/// Request body for updating a project.
/// </summary>
public sealed record TerraScaleUpdateProjectRequest(string Name);

/// <summary>
/// Project metadata returned by TerraScale project endpoints.
/// </summary>
public sealed record TerraScaleProject(
    string ProjectId,
    string OrganizationId,
    string Name,
    string? OwnerUserId,
    string? CreatedByUserId,
    DateTimeOffset? OwnershipAssignedAt,
    IReadOnlyList<string> DefaultPermissionKeys,
    string Pk,
    string Sk,
    string EntityType,
    DateTimeOffset CreatedAt,
    long Version);
