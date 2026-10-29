using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>An organization: the tenant, which owns billing, and the top-level owner of projects.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="Name">Display name, unique among the organizations created by <paramref name="CreatedBy"/> (case-insensitive).</param>
/// <param name="CreatedBy">User id (from ts-auth) of the creator, its first member.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Plan">The organization's plan; <see cref="PlanTier.Hobby"/> until set.</param>
public sealed record Organization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, PlanTier Plan = PlanTier.Hobby);

/// <summary>A project inside an organization: a folder of databases.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Name">Display name, unique within the organization (case-insensitive).</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record Project(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

/// <summary>A database registered in a project.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">
/// Database name (same rules as data API database names), unique within the project. The data API addresses the
/// database as <c>/v1/projects/{ProjectId}/databases/{Name}</c>.
/// </param>
/// <param name="BranchedFrom">Id of the database this one was branched from, if any.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="HomeRegion">
/// The region the database is created and managed in (docs/cross-region.md); <see langword="null"/> for a database
/// registered before placement existed, whose home is the region that holds it.
/// </param>
/// <param name="ReplicaRegions">The other regions that hold a full copy; <see langword="null"/> for none.</param>
/// <param name="PlacementVersion">Raised by the home region on every placement change; the highest one wins elsewhere.</param>
/// <param name="AllRegions">Every serving region holds a full copy, including regions that join later; no replica regions are then recorded.</param>
/// <param name="PreviousName">
/// The name this database lost to <paramref name="ConflictOf"/> when two regions accepted it at once; <see langword="null"/>
/// for none (docs/terrabase/database-name-conflicts.md).
/// </param>
/// <param name="ConflictOf">Id of the database that kept <paramref name="PreviousName"/>; <see langword="null"/> for none.</param>
public sealed record ManagedDatabase(
    string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt,
    string? HomeRegion = null, IReadOnlyList<string>? ReplicaRegions = null, long PlacementVersion = 0, bool AllRegions = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConflictOf = null)
{
    /// <summary>The home region, as a member of <paramref name="region"/> reads it.</summary>
    public string Home(string region) => HomeRegion ?? region;

    /// <summary>The replica regions.</summary>
    public IReadOnlyList<string> Replicas => ReplicaRegions ?? [];

    /// <summary>The home region and the replica regions, as a member of <paramref name="region"/> reads them.</summary>
    public IReadOnlyList<string> Regions(string region) => [Home(region), .. Replicas];

    /// <summary>Whether <paramref name="region"/> serves the database.</summary>
    public bool PlacedIn(string region) => AllRegions || Home(region) == region || Replicas.Contains(region);

    /// <summary>The placement, as a member of <paramref name="region"/> reads it.</summary>
    public DatabasePlacement Placement(string region) => new(Home(region), Replicas, PlacementVersion, AllRegions);
}

/// <summary>The created organization, or why not: its name or a client-chosen id is taken.</summary>
public readonly union CreateOrganizationResult(Organization, Conflict, Unavailable);

/// <summary>A page of the user's organizations.</summary>
public readonly union ListOrganizationsResult(ManagementPage<Organization>, Unavailable);

/// <summary>An organization of the user.</summary>
public readonly union GetOrganizationResult(Organization, NotFound, Unavailable);

/// <summary>The organization with its new plan, or why not.</summary>
public readonly union SetPlanResult(Organization, NotFound, Forbidden, Unavailable);

/// <summary>Whether the organization was deleted: it may still have projects.</summary>
public readonly union DeleteOrganizationResult(Done, NotFound, Forbidden, Conflict, Unavailable);

/// <summary>The created project, or why not: its name or a client-chosen id is taken.</summary>
public readonly union CreateProjectResult(Project, NotFound, Forbidden, Conflict, Unavailable);

/// <summary>A page of an organization's projects.</summary>
public readonly union ListProjectsResult(ManagementPage<Project>, NotFound, Unavailable);

/// <summary>A project of one of the user's organizations.</summary>
public readonly union GetProjectResult(Project, NotFound, Unavailable);

/// <summary>Whether the project was deleted: it may still have databases or API keys.</summary>
public readonly union DeleteProjectResult(Done, NotFound, Forbidden, Conflict, Unavailable);

/// <summary>The created database, or why not: its name is taken.</summary>
public readonly union CreateDatabaseResult(ManagedDatabase, NotFound, Forbidden, Conflict, Unavailable);

/// <summary>The database with its new placement, or why not: it is managed in another region.</summary>
public readonly union SetDatabaseRegionsResult(ManagedDatabase, NotFound, Forbidden, Conflict, Unavailable);

/// <summary>A database as this region records it.</summary>
public readonly union FindDatabaseRecordResult(ManagedDatabase, NotFound, Unavailable);

/// <summary>A page of a project's databases.</summary>
public readonly union ListDatabasesResult(ManagementPage<ManagedDatabase>, NotFound, Unavailable);

/// <summary>A database of one of the user's projects.</summary>
public readonly union GetDatabaseResult(ManagedDatabase, NotFound, Unavailable);

/// <summary>The tenant of a database the user reaches.</summary>
public readonly union DatabaseTenantResult(TenantContext, NotFound, Unavailable);

/// <summary>The tenant of a database the user may change.</summary>
public readonly union WritableDatabaseTenantResult(TenantContext, NotFound, Forbidden, Unavailable);

/// <summary>A project the user may change.</summary>
public readonly union WritableProjectResult(Project, NotFound, Forbidden, Unavailable);

/// <summary>Whether the database was deleted: it is managed in another region, or still has replica regions.</summary>
public readonly union DeleteDatabaseResult(Done, NotFound, Forbidden, Conflict, Unavailable);

/// <summary>The branch, or why not: the name is taken or the source is managed in another region.</summary>
public readonly union BranchDatabaseResult(ManagedDatabase, NotFound, Forbidden, Conflict, QuotaExceeded, Unavailable);

/// <summary>The copy, or why not: the name is taken.</summary>
public readonly union CopyDatabaseResult(ManagedDatabase, NotFound, Forbidden, Conflict, QuotaExceeded, Unavailable);

/// <summary>One page of a list in id (creation) order.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">The items of this page.</param>
/// <param name="Next">Cursor for the next page (<c>after</c>); <see langword="null"/> on the last page.</param>
public sealed record ManagementPage<T>(IReadOnlyList<T> Items, string? Next);

internal interface IManagementService
{
    /// <summary>The exact live database an existing owner or admin may recover; never creates catalog state.</summary>
    ValueTask<WritableDatabaseTenantResult> GetRecoveryDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, string? authorityToken, CancellationToken cancellationToken);

    ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the user's organizations after <paramref name="after"/>.</summary>
    ValueTask<ListOrganizationsResult> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads an organization.</summary>
    ValueTask<GetOrganizationResult> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    ValueTask<SetPlanResult> SetPlanAsync(string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken);

    ValueTask<DeleteOrganizationResult> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Creates a project in an organization.</summary>
    ValueTask<CreateProjectResult> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the projects of an organization.</summary>
    ValueTask<ListProjectsResult> ListProjectsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a project.</summary>
    ValueTask<GetProjectResult> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Deletes a project that has no databases, API keys or customer backup destination.</summary>
    ValueTask<DeleteProjectResult> DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Creates an empty database in a project, placed in every region when <paramref name="allRegions"/>.</summary>
    ValueTask<CreateDatabaseResult> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken, IReadOnlyList<string>? replicaRegions = null,
        bool allRegions = false);

    /// <summary>
    /// Sets the replica regions of a database whose home is this region (docs/cross-region.md): <see cref="ManagementErrors.WrongRegion"/> in any
    /// other region. The caller validated <paramref name="replicaRegions"/>, empty when <paramref name="allRegions"/>.
    /// </summary>
    ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionsAsync(
        string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken,
        bool allRegions = false);

    /// <summary>
    /// Renames a database whose home is this region, keeping its id, data, placement and keys
    /// (docs/terrabase/database-rename.md): <see cref="ManagementErrors.NameTaken"/> when this region knows another holder of
    /// <paramref name="name"/>, <see cref="ManagementErrors.WrongRegion"/> in any other region. The caller validated the name.
    /// </summary>
    ValueTask<SetDatabaseRegionsResult> RenameDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken);

    ValueTask<FindDatabaseRecordResult> FindDatabaseRecordAsync(string organizationId, string projectId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the databases of a project.</summary>
    ValueTask<ListDatabasesResult> ListDatabasesAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a database.</summary>
    ValueTask<GetDatabaseResult> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    ValueTask<DatabaseTenantResult> GetDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    ValueTask<WritableDatabaseTenantResult> GetWritableDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    ValueTask<WritableProjectResult> GetWritableProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a database and its data. Its files and the expiry of its backups are owed to the storage sweep from the
    /// catalog commit on, so a failed or interrupted deletion of the files is retried.
    /// </summary>
    ValueTask<DeleteDatabaseResult> DeleteDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    /// <summary>Creates a database in the same project as a snapshot copy of an existing one.</summary>
    ValueTask<BranchDatabaseResult> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a database in a project as a snapshot copy of the engine database <paramref name="sourceId"/> of the same
    /// project (for example a backup snapshot); <see cref="NotFound"/> when the project or the source does not exist.
    /// </summary>
    ValueTask<CopyDatabaseResult> CopyDatabaseAsync(
        string userId, string organizationId, string projectId, string sourceId, string name, string? branchedFrom, CancellationToken cancellationToken);
}
