namespace TerraBase.Tenancy;

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
    /// Whether the catalog registers <paramref name="database"/> (its organization, project and database ids):
    /// <see langword="null"/> when this member's catalog is at a version this build does not read, so neither is known.
    /// </summary>
    ValueTask<bool?> RegisteredAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// The plan of organization <paramref name="organizationId"/>: <see cref="PlanTier.Hobby"/> when it does not exist (or no
    /// longer does), or when this member's catalog is at a version this build does not read.
    /// </summary>
    ValueTask<PlanTier> PlanAsync(string organizationId, CancellationToken cancellationToken);
}
