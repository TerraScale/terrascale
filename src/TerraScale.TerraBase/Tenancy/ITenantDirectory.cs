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
    /// Whether this member sends client replication for <paramref name="database"/>: registered, or being created (its files
    /// exist under a pending cleanup intent until its registration commits). Peers refuse every other engine database:
    /// backup snapshots, restore staging and files the catalog no longer names. <see langword="null"/> as for
    /// <see cref="RegisteredAsync"/>.
    /// </summary>
    ValueTask<bool?> ReplicatesAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>The name of <paramref name="database"/>, or <see langword="null"/> when the catalog does not register it.</summary>
    ValueTask<string?> DatabaseNameAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Registers <paramref name="database"/> under its organization, project and database ids and <paramref name="name"/>, as
    /// another region holds it, when this region registers its organization and project under the same ids
    /// (docs/cross-region.md). Only a catalog primary registers.
    /// </summary>
    ValueTask<DatabaseAdoption> AdoptDatabaseAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>
    /// The plan of organization <paramref name="organizationId"/>: <see cref="PlanTier.Hobby"/> when it does not exist (or no
    /// longer does), or when this member's catalog is at a version this build does not read.
    /// </summary>
    ValueTask<PlanTier> PlanAsync(string organizationId, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="ITenantDirectory.AdoptDatabaseAsync"/>.</summary>
public enum DatabaseAdoption
{
    /// <summary>The database is registered now.</summary>
    Registered,

    /// <summary>This region does not register its organization and project under the same ids: the project does not replicate here.</summary>
    NotReplicated,

    /// <summary>This region used the database id before: a database deleted here is not brought back.</summary>
    Deleted,

    /// <summary>Another database of the project has the name here.</summary>
    NameTaken,
}
