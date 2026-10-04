namespace TerraScale.TerraBase.Features.Management;

/// <summary>An organization: the top-level owner of projects.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="Name">Display name, unique among organizations (case-insensitive).</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record Organization(string Id, string Name, DateTimeOffset CreatedAt);

/// <summary>A project inside an organization; it owns databases.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Name">Display name, unique within the organization (case-insensitive).</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record Project(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

/// <summary>A database registered in a project.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name (same rules as data API database names), unique within the project.</param>
/// <param name="StorageName">The name the data API (<c>/v1/databases/{database}</c>) addresses this database by.</param>
/// <param name="BranchedFrom">Id of the database this one was branched from, if any.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record ManagedDatabase(string Id, string ProjectId, string Name, string StorageName, string? BranchedFrom, DateTimeOffset CreatedAt);

/// <summary>Why a management operation did not succeed.</summary>
public enum ManagementError
{
    /// <summary>Succeeded.</summary>
    None,

    /// <summary>The addressed resource, or one of its parents, does not exist.</summary>
    NotFound,

    /// <summary>Another resource with the same parent already has the name.</summary>
    NameTaken,

    /// <summary>The resource still has children; delete them first.</summary>
    NotEmpty,
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
public interface IManagementService
{
    /// <summary>Creates an organization.</summary>
    ValueTask<ManagementResult<Organization>> CreateOrganizationAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists organizations after <paramref name="after"/>.</summary>
    ValueTask<ManagementPage<Organization>> ListOrganizationsAsync(string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads an organization.</summary>
    ValueTask<Organization?> GetOrganizationAsync(string organizationId, CancellationToken cancellationToken);

    /// <summary>Renames an organization.</summary>
    ValueTask<ManagementResult<Organization>> RenameOrganizationAsync(string organizationId, string name, CancellationToken cancellationToken);

    /// <summary>Deletes an organization that has no projects.</summary>
    ValueTask<ManagementError> DeleteOrganizationAsync(string organizationId, CancellationToken cancellationToken);

    /// <summary>Reads the organization <paramref name="userId"/> last switched to; <see langword="null"/> when none or deleted.</summary>
    ValueTask<Organization?> GetCurrentOrganizationAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Switches <paramref name="userId"/> to an existing organization.</summary>
    ValueTask<ManagementResult<Organization>> SetCurrentOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Creates a project in an organization.</summary>
    ValueTask<ManagementResult<Project>> CreateProjectAsync(string organizationId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the projects of an organization; <see langword="null"/> when it does not exist.</summary>
    ValueTask<ManagementPage<Project>?> ListProjectsAsync(string organizationId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a project.</summary>
    ValueTask<Project?> GetProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Renames a project.</summary>
    ValueTask<ManagementResult<Project>> RenameProjectAsync(string organizationId, string projectId, string name, CancellationToken cancellationToken);

    /// <summary>Deletes a project that has no databases.</summary>
    ValueTask<ManagementError> DeleteProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Creates an empty database in a project.</summary>
    ValueTask<ManagementResult<ManagedDatabase>> CreateDatabaseAsync(
        string organizationId, string projectId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the databases of a project; <see langword="null"/> when it does not exist.</summary>
    ValueTask<ManagementPage<ManagedDatabase>?> ListDatabasesAsync(
        string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a database.</summary>
    ValueTask<ManagedDatabase?> GetDatabaseAsync(string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    /// <summary>Deletes a database and its data.</summary>
    ValueTask<ManagementError> DeleteDatabaseAsync(string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    /// <summary>Creates a database in the same project as a snapshot copy of an existing one.</summary>
    ValueTask<ManagementResult<ManagedDatabase>> BranchDatabaseAsync(
        string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken);
}
