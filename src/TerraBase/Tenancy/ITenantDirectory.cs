using TerraBase.Errors;

namespace TerraBase.Tenancy;

/// <summary>A database and placement resolved in one catalog snapshot.</summary>
/// <param name="Database">The database identity.</param>
/// <param name="Placement">Its recorded placement.</param>
public sealed record ResolvedProjectDatabase(TenantContext Database, DatabasePlacement Placement);

/// <summary>A project database, its known absence, or an unreadable catalog.</summary>
public readonly union ResolveProjectDatabaseResult(ResolvedProjectDatabase, NotFound, Unavailable);

/// <summary>A local database prepared for use, known absence, or an unreadable catalog.</summary>
public readonly union EnsureLocalDatabaseResult(Done, NotFound, Unavailable);

/// <summary>A database of a tenant, or why it does not resolve.</summary>
public readonly union FindDatabaseResult(TenantContext, NotFound, Unavailable);

/// <summary>A database created or found by <see cref="ITenantDirectory.CreateDatabaseAsync"/>.</summary>
/// <param name="Database">The database.</param>
/// <param name="Created">Whether this call created it; <see langword="false"/> when it existed.</param>
public sealed record ProvisionedDatabase(TenantContext Database, bool Created);

/// <summary>A database created or found, or why there is none.</summary>
public readonly union ProvisionDatabaseResult(ProvisionedDatabase, NotFound, Unavailable);

/// <summary>Known registration in this region, or an unreadable catalog.</summary>
public readonly union RegisteredDatabaseResult(bool, Unavailable);

/// <summary>Known replication eligibility, or an unreadable catalog.</summary>
public readonly union ReplicatesDatabaseResult(bool, Unavailable);

/// <summary>A database's name and placement in this region's catalog.</summary>
/// <param name="Name">Its recorded name.</param>
/// <param name="Placement">Its recorded placement.</param>
public sealed record TenantDatabaseRecord(string Name, DatabasePlacement Placement);

/// <summary>A recorded database, known absence, or an unreadable catalog.</summary>
public readonly union DatabaseRecordResult(TenantDatabaseRecord, NotFound, Unavailable);

/// <summary>A bounded page of registrations, or an unreadable catalog.</summary>
public readonly union ReadDatabaseRecordsResult(TenantDatabasePage, Unavailable);

/// <summary>Registration, missing project, conflicting name or deleted id, or an unreadable catalog.</summary>
public readonly union AdoptDatabaseResult(Done, NotFound, Conflict, Unavailable);

/// <summary>A completed placement check.</summary>
/// <param name="Updated">Whether a newer placement was taken.</param>
public sealed record PlacementUpdate(bool Updated);

/// <summary>A completed placement check, or an unreadable catalog.</summary>
public readonly union UpdatePlacementResult(PlacementUpdate, Unavailable);

/// <summary>A completed deletion check.</summary>
/// <param name="Forgotten">Whether this call deleted the record and copy.</param>
public sealed record DatabaseForgotten(bool Forgotten);

/// <summary>A completed deletion check, or an unreadable catalog.</summary>
public readonly union ForgetDatabaseResult(DatabaseForgotten, Unavailable);

/// <summary>An organization's plan, or an unreadable catalog.</summary>
public readonly union TenantPlanResult(PlanTier, Unavailable);

/// <summary>Whether this member can interpret its tenant catalog.</summary>
public readonly union TenantCatalogResult(Done, Unavailable);

/// <summary>
/// Resolves and registers databases for organization API keys and scoped project access tokens, without user
/// membership checks. A project is found only among its organization's projects and a database only among that
/// project's databases, so ids from another tenant never resolve.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>Checks catalog readability before publishing an inventory, including an empty one.</summary>
    ValueTask<TenantCatalogResult> CheckCatalogAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a database name and its placement from one catalog snapshot, without initializing local storage.
    /// The caller must validate the token's project, database restriction and operation scope first.
    /// </summary>
    ValueTask<ResolveProjectDatabaseResult> ResolveProjectDatabaseAsync(string projectId, string database, CancellationToken cancellationToken);

    /// <summary>
    /// Initializes local storage for a registered database identity on a catalog follower, without resolving its name
    /// again. The caller must validate the request and placement first and revalidate the catalog before using storage.
    /// </summary>
    ValueTask<EnsureLocalDatabaseResult> EnsureLocalDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// The database named <paramref name="database"/> in project <paramref name="projectId"/> of organization
    /// <paramref name="organizationId"/>; <see cref="NotFound"/> when the organization, the project in it or the
    /// database in the project does not exist, <see cref="Unavailable"/> while the catalog is at a version this build cannot use.
    /// </summary>
    ValueTask<FindDatabaseResult> FindDatabaseAsync(string organizationId, string projectId, string database, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the database named <paramref name="database"/> in project <paramref name="projectId"/> of organization
    /// <paramref name="organizationId"/>, or finds it (<c>Created</c> is <see langword="false"/>) when it exists;
    /// <see cref="NotFound"/> when the organization or the project in it does not exist, <see cref="Unavailable"/> as for
    /// <see cref="FindDatabaseAsync"/>.
    /// </summary>
    ValueTask<ProvisionDatabaseResult> CreateDatabaseAsync(
        string organizationId, string projectId, string database, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the catalog registers <paramref name="database"/> (its organization, project and database ids) for this region:
    /// it is recorded and its placement names this region. <see cref="Unavailable"/> when this member's catalog is at a version
    /// this build does not read, so neither is known.
    /// </summary>
    ValueTask<RegisteredDatabaseResult> RegisteredAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this member sends client replication for <paramref name="database"/>: registered, or being created (its files
    /// exist under a pending cleanup intent until its registration commits). Peers refuse every other engine database:
    /// backup snapshots, restore staging and files the catalog no longer names. <see cref="Unavailable"/> as for
    /// <see cref="RegisteredAsync"/>.
    /// </summary>
    ValueTask<ReplicatesDatabaseResult> ReplicatesAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// The name and placement of <paramref name="database"/> as this region's catalog records it, <see cref="NotFound"/>
    /// when it records none, or <see cref="Unavailable"/> when its version cannot be read.
    /// </summary>
    ValueTask<DatabaseRecordResult> DatabaseRecordAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// One bounded page of registrations whose home is <paramref name="homeRegion"/>, including records without a local
    /// file, after the previous page's cursor. Read it before asking the home region for heads, so a newer registration
    /// cannot be mistaken for a deletion there. The storage reader is released before this method returns.
    /// An unreadable catalog returns <see cref="Unavailable"/>, never an empty page.
    /// </summary>
    ValueTask<ReadDatabaseRecordsResult> ReadDatabaseRecordsAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="database"/> under its organization, project and database ids, <paramref name="name"/> and
    /// <paramref name="placement"/>, as another region holds it, when this region registers its organization and project under
    /// the same ids (docs/cross-region.md). A database placed here also gets its file; one placed elsewhere only its record,
    /// so this region can say where it is served. Only a catalog primary registers. <see cref="Unavailable"/> while
    /// its catalog is at a version this build cannot use.
    /// </summary>
    ValueTask<AdoptDatabaseResult> AdoptDatabaseAsync(TenantContext database, string name, DatabasePlacement placement, CancellationToken cancellationToken);

    /// <summary>
    /// Takes <paramref name="placement"/>, which the database's home region decided, when it is newer than the recorded one;
    /// returns its success disposition or <see cref="Unavailable"/> for an unreadable catalog. Only a catalog primary records.
    /// </summary>
    ValueTask<UpdatePlacementResult> UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes this region's record and copy of <paramref name="database"/>, which its home region (another one) deleted, so
    /// that nothing brings it back; returns its success disposition or <see cref="Unavailable"/> for an unreadable catalog. Only a catalog
    /// primary deletes; its followers follow its catalog.
    /// </summary>
    ValueTask<ForgetDatabaseResult> ForgetDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// The plan of organization <paramref name="organizationId"/>: <see cref="PlanTier.Hobby"/> when it does not exist (or no
    /// longer does); <see cref="Unavailable"/> when this member's catalog is at a version this build does not read.
    /// </summary>
    ValueTask<TenantPlanResult> PlanAsync(string organizationId, CancellationToken cancellationToken);
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
