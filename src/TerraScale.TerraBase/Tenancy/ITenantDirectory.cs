namespace TerraScale.TerraBase.Tenancy;

/// <summary>
/// Resolves and registers the databases of a tenant for callers that act for an organization, not for a user
/// (API keys). Every lookup starts at the organization: a project is found only among that organization's
/// projects and a database only among that project's, so ids from another tenant never resolve.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>
    /// The database named <paramref name="database"/> in project <paramref name="projectId"/> of organization
    /// <paramref name="organizationId"/>; <see langword="null"/> when the organization, the project in it or the
    /// database in the project does not exist.
    /// </summary>
    ValueTask<TenantContext?> FindDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the database named <paramref name="database"/> in project <paramref name="projectId"/> of organization
    /// <paramref name="organizationId"/>, or finds it (<c>Created</c> is <see langword="false"/>) when it exists;
    /// <see langword="null"/> when the organization or the project in it does not exist.
    /// </summary>
    ValueTask<(TenantContext Database, bool Created)?> CreateDatabaseAsync(
        string organizationId, string projectId, string database, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the organization exists and, when given, <paramref name="projectId"/> is one of its projects and
    /// <paramref name="databaseId"/> one of that project's databases.
    /// </summary>
    ValueTask<bool> ExistsAsync(string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken);
}
