using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>An organization: the tenant, which owns billing, and the top-level owner of projects.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="Name">Display name, unique among the organizations created by <paramref name="CreatedBy"/> (case-insensitive).</param>
/// <param name="CreatedBy">User id (from ts-auth) of the creator, its first member.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Plan">The organization's plan; <see cref="PlanTier.Hobby"/> until set.</param>
public sealed record Organization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, PlanTier Plan = PlanTier.Hobby);

/// <summary>A project inside an organization: a folder of databases.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Name">Display name, unique within the organization (case-insensitive).</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record Project(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

/// <summary>A database registered in a project.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">
/// Database name (same rules as data API database names), unique within the project. The data API addresses the
/// database as <c>/v1/projects/{ProjectId}/databases/{Name}</c>.
/// </param>
/// <param name="BranchedFrom">Id of the database this one was branched from, if any.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record ManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt);

/// <summary>Why a management operation did not succeed.</summary>
public enum ManagementError
{
    /// <summary>Succeeded.</summary>
    None,

    /// <summary>The addressed resource, or one of its parents, does not exist or the user is not a member of its organization.</summary>
    NotFound,

    /// <summary>Another resource with the same parent already has the name.</summary>
    NameTaken,

    /// <summary>The resource still has children; delete them first.</summary>
    NotEmpty,

    /// <summary>The acting user is a member, but its role does not allow the operation.</summary>
    Forbidden,

    /// <summary>The change would leave the organization without an owner.</summary>
    LastOwner,

    /// <summary>The user is already a member of the organization.</summary>
    AlreadyMember,

    /// <summary>An unexpired invitation for the email address is pending.</summary>
    InvitationPending,

    /// <summary>The member is already an owner.</summary>
    AlreadyOwner,

    /// <summary>The invitation expired.</summary>
    Expired,

    /// <summary>A client-chosen id is already used by another resource.</summary>
    IdTaken,

    /// <summary>The invitation is no longer pending: it was accepted, declined or revoked.</summary>
    InvitationClosed,

    /// <summary>Another invitation already has the token hash.</summary>
    TokenTaken,
}

/// <summary>A value or the reason there is none.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Value">The value when <paramref name="Error"/> is <see cref="ManagementError.None"/>.</param>
/// <param name="Error">The failure reason.</param>
public readonly record struct ManagementResult<T>(T? Value, ManagementError Error)
{
    /// <summary>Wraps a failure.</summary>
    public static implicit operator ManagementResult<T>(ManagementError error)
    {
        return new(default, error);
    }

    /// <summary>Wraps a value.</summary>
    public static implicit operator ManagementResult<T>(T value)
    {
        return new(value, ManagementError.None);
    }
}

/// <summary>One page of a list in id (creation) order.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">The items of this page.</param>
/// <param name="Next">Cursor for the next page (<c>after</c>); <see langword="null"/> on the last page.</param>
public sealed record ManagementPage<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>
/// Organizations, projects and the databases they own, for the dashboard. Ids are opaque to callers;
/// names are validated by the endpoints before they reach this service.
/// </summary>
/// <remarks>
/// Every method acts as <c>userId</c> (the ts-auth user id) and authorizes in the same transaction as the
/// operation: an organization the user is not a member of, and everything under it, is <see cref="ManagementError.NotFound"/>.
/// </remarks>
public interface IManagementService
{
    /// <summary>Creates an organization with the user as its first member, an <see cref="OrganizationRole.Owner"/>.</summary>
    ValueTask<ManagementResult<Organization>> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the user's organizations after <paramref name="after"/>.</summary>
    ValueTask<ManagementPage<Organization>> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads an organization.</summary>
    ValueTask<Organization?> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Renames an organization. Needs <see cref="OrganizationRole.Admin"/>.</summary>
    ValueTask<ManagementResult<Organization>> RenameOrganizationAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken);

    /// <summary>Sets the plan of an organization. Needs <see cref="OrganizationRole.Owner"/>.</summary>
    ValueTask<ManagementResult<Organization>> SetPlanAsync(string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an organization that has no projects, with its memberships, invitations, ownership transfer and
    /// audit log. Needs <see cref="OrganizationRole.Owner"/>.
    /// </summary>
    ValueTask<ManagementError> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Reads the organization the user last switched to; <see langword="null"/> when none or no longer a member.</summary>
    ValueTask<Organization?> GetCurrentOrganizationAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Switches the user to one of their organizations.</summary>
    ValueTask<ManagementResult<Organization>> SetCurrentOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Creates a project in an organization.</summary>
    ValueTask<ManagementResult<Project>> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the projects of an organization; <see langword="null"/> when it does not exist.</summary>
    ValueTask<ManagementPage<Project>?> ListProjectsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a project.</summary>
    ValueTask<Project?> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Renames a project.</summary>
    ValueTask<ManagementResult<Project>> RenameProjectAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken);

    /// <summary>Deletes a project that has no databases or API keys.</summary>
    ValueTask<ManagementError> DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Creates an empty database in a project.</summary>
    ValueTask<ManagementResult<ManagedDatabase>> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the databases of a project; <see langword="null"/> when it does not exist.</summary>
    ValueTask<ManagementPage<ManagedDatabase>?> ListDatabasesAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a database.</summary>
    ValueTask<ManagedDatabase?> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    /// <summary>
    /// The tenant of a database the user reaches through membership of its organization: organization and plan, project
    /// and database, resolved in the same catalog read as the membership check; <see langword="null"/> when any is missing.
    /// </summary>
    ValueTask<TenantContext?> GetDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="GetDatabaseTenantAsync"/> for a change to the database or its tooling state: <see cref="ManagementError.Forbidden"/>
    /// for a <see cref="OrganizationRole.Viewer"/>, which is read-only.
    /// </summary>
    ValueTask<ManagementResult<TenantContext>> GetWritableDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    /// <summary>
    /// A project whose state the user may change: <see cref="ManagementError.NotFound"/> as for <see cref="GetProjectAsync"/>,
    /// <see cref="ManagementError.Forbidden"/> for a <see cref="OrganizationRole.Viewer"/>.
    /// </summary>
    ValueTask<ManagementResult<Project>> GetWritableProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a database and its data. Its files and the expiry of its backups are owed to the storage sweep from the
    /// catalog commit on, so a failed or interrupted deletion of the files is retried.
    /// </summary>
    ValueTask<ManagementError> DeleteDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    /// <summary>Creates a database in the same project as a snapshot copy of an existing one.</summary>
    ValueTask<ManagementResult<ManagedDatabase>> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a database in a project as a snapshot copy of the engine database <paramref name="sourceId"/> of the same
    /// project (for example a backup snapshot); <see cref="ManagementError.NotFound"/> when the project or the source does not exist.
    /// </summary>
    ValueTask<ManagementResult<ManagedDatabase>> CopyDatabaseAsync(
        string userId, string organizationId, string projectId, string sourceId, string name, string? branchedFrom, CancellationToken cancellationToken);
}
