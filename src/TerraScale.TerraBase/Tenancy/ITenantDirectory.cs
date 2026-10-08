namespace TerraBase.Tenancy;

/// <summary>
/// Resolves and registers the databases of a tenant for callers that act for an organization, not for a user
/// (API keys). Every lookup starts at the organization: a project is found only among that organization's
/// projects and a database only among that project's, so ids from another tenant never resolve.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>Resolves a database by its owning project, after the caller's JWT project grant was checked.</summary>
    ValueTask<TenantContext?> FindProjectDatabaseAsync(string projectId, string database, CancellationToken cancellationToken);

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
    /// Whether the catalog registers <paramref name="database"/> (its organization, project and database ids) for this region:
    /// it is recorded and its placement names this region. <see langword="null"/> when this member's catalog is at a version
    /// this build does not read, so neither is known.
    /// </summary>
    ValueTask<bool?> RegisteredAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this member sends client replication for <paramref name="database"/>: registered, or being created (its files
    /// exist under a pending cleanup intent until its registration commits). Peers refuse every other engine database:
    /// backup snapshots, restore staging and files the catalog no longer names. <see langword="null"/> as for
    /// <see cref="RegisteredAsync"/>.
    /// </summary>
    ValueTask<bool?> ReplicatesAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// The name and placement of <paramref name="database"/> as this region's catalog records it, or <see langword="null"/>
    /// when it records none.
    /// </summary>
    ValueTask<(string Name, DatabasePlacement Placement)?> DatabaseRecordAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// One bounded page of registrations whose home is <paramref name="homeRegion"/>, including records without a local
    /// file, after the previous page's cursor. Read it before asking the home region for heads, so a newer registration
    /// cannot be mistaken for a deletion there. The storage reader is released before this method returns.
    /// </summary>
    ValueTask<TenantDatabasePage> ReadDatabaseRecordsAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="database"/> under its organization, project and database ids, <paramref name="name"/> and
    /// <paramref name="placement"/>, as another region holds it, when this region registers its organization and project under
    /// the same ids (docs/cross-region.md). A database placed here also gets its file; one placed elsewhere only its record,
    /// so this region can say where it is served. Only a catalog primary registers.
    /// </summary>
    ValueTask<DatabaseAdoption> AdoptDatabaseAsync(TenantContext database, string name, DatabasePlacement placement, CancellationToken cancellationToken);

    /// <summary>
    /// Takes <paramref name="placement"/>, which the database's home region decided, when it is newer than the recorded one;
    /// whether it was taken. Only a catalog primary records.
    /// </summary>
    ValueTask<bool> UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes this region's record and copy of <paramref name="database"/>, which its home region (another one) deleted, so
    /// that nothing brings it back; whether it did. Only a catalog primary deletes; its followers follow its catalog.
    /// </summary>
    ValueTask<bool> ForgetDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// The plan of organization <paramref name="organizationId"/>: <see cref="PlanTier.Hobby"/> when it does not exist (or no
    /// longer does), or when this member's catalog is at a version this build does not read.
    /// </summary>
    ValueTask<PlanTier> PlanAsync(string organizationId, CancellationToken cancellationToken);
}

/// <summary>Where a database is served (docs/cross-region.md): its home region and replica regions.</summary>
/// <param name="Home">The region the database is created and managed in.</param>
/// <param name="Replicas">The other regions that hold a full copy.</param>
/// <param name="Version">Raised by the home region on every change; the highest one wins elsewhere.</param>
public sealed record DatabasePlacement(string Home, IReadOnlyList<string> Replicas, long Version)
{
    /// <summary>Whether <paramref name="region"/> serves the database.</summary>
    public bool Includes(string region) => Home == region || Replicas.Contains(region);

    /// <summary>The home region, then the replica regions.</summary>
    public IReadOnlyList<string> Regions => [Home, .. Replicas];
}

/// <summary>The outcome of <see cref="ITenantDirectory.AdoptDatabaseAsync"/>.</summary>
public enum DatabaseAdoption
{
    /// <summary>The database is recorded now (with its file when this region serves it).</summary>
    Registered,

    /// <summary>This region does not register its organization and project under the same ids: the project does not replicate here.</summary>
    NotReplicated,

    /// <summary>This region used the database id before: a database deleted here is not brought back.</summary>
    Deleted,

    /// <summary>Another database of the project has the name here.</summary>
    NameTaken,
}
