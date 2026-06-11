namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale repository endpoints.
/// </summary>
public sealed class TerraScaleRepositoriesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleRepositoriesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists repositories visible to the current credentials.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleRepository>> ListAsync(
        CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyList<TerraScaleRepository>>(
            HttpMethod.Get,
            "/api/v1/repositories",
            body: null,
            cancellationToken);

    /// <summary>
    /// Gets a repository by id.
    /// </summary>
    public Task<TerraScaleRepository> GetAsync(
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        return client.SendAsync<TerraScaleRepository>(
            HttpMethod.Get,
            $"/api/v1/repositories/{Escape(repositoryId)}",
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a repository, optionally targeting a database or database environment.
    /// </summary>
    public Task<TerraScaleRepository> CreateAsync(
        string name,
        string? collection = null,
        bool schemaValidationEnabled = true,
        string? schema = null,
        string? databaseId = null,
        string? environmentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var selectedDatabaseId = NormalizeOptional(databaseId) ?? NormalizeOptional(client.DatabaseId);
        var selectedEnvironmentId = NormalizeOptional(environmentId) ?? NormalizeOptional(client.EnvironmentId);
        if (!string.IsNullOrWhiteSpace(selectedEnvironmentId) &&
            string.IsNullOrWhiteSpace(selectedDatabaseId))
        {
            throw new ArgumentException("A database id is required when creating a repository in an environment.", nameof(databaseId));
        }

        return client.SendAsync<TerraScaleRepository>(
            HttpMethod.Post,
            "/api/v1/repositories",
            new TerraScaleCreateRepositoryRequest(
                name.Trim(),
                selectedDatabaseId,
                selectedEnvironmentId,
                NormalizeOptional(collection),
                schemaValidationEnabled,
                NormalizeOptional(schema)),
            cancellationToken);
    }

    /// <summary>
    /// Deletes a repository by id.
    /// </summary>
    public Task<TerraScaleDeletedRepository> DeleteAsync(
        string repositoryId,
        bool deleteDocuments = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        return client.SendAsync<TerraScaleDeletedRepository>(
            HttpMethod.Delete,
            $"/api/v1/repositories/{Escape(repositoryId)}?deleteDocuments={deleteDocuments.ToString().ToLowerInvariant()}",
            body: null,
            cancellationToken);
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record TerraScaleCreateRepositoryRequest(
    string Name,
    string? DatabaseId,
    string? EnvironmentId,
    string? Collection,
    bool SchemaValidationEnabled,
    string? Schema);

/// <summary>
/// Repository metadata returned by TerraScale.
/// </summary>
public sealed record TerraScaleRepository(
    string RepositoryId,
    string Name,
    string DatabaseId,
    string? EnvironmentId,
    string Collection,
    bool SchemaValidationEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Result of deleting a repository.
/// </summary>
public sealed record TerraScaleDeletedRepository(
    string RepositoryId,
    string Name,
    bool Deleted,
    long DocumentsDeleted);
