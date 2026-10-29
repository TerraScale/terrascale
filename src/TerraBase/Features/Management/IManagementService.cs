using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

public sealed record Organization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, PlanTier Plan = PlanTier.Hobby);

public sealed record Project(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

public sealed record ManagedDatabase(
    string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt,
    string? HomeRegion = null, IReadOnlyList<string>? ReplicaRegions = null, long PlacementVersion = 0, bool AllRegions = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConflictOf = null)
{
    public string Home(string region) => HomeRegion ?? region;

    public IReadOnlyList<string> Replicas => ReplicaRegions ?? [];

    public IReadOnlyList<string> Regions(string region) => [Home(region)];

    public bool PlacedIn(string region) => Home(region) == region;

    public DatabasePlacement Placement(string region) => new(Home(region), [], PlacementVersion);
}

public readonly union CreateOrganizationResult(Organization, Conflict, Unavailable);

public readonly union ListOrganizationsResult(ManagementPage<Organization>, Unavailable);

public readonly union GetOrganizationResult(Organization, NotFound, Unavailable);

public readonly union RenameOrganizationResult(Organization, NotFound, Forbidden, Conflict, Unavailable);

public readonly union SetPlanResult(Organization, NotFound, Forbidden, Unavailable);

public readonly union DeleteOrganizationResult(Done, NotFound, Forbidden, Conflict, Unavailable);

public readonly union GetCurrentOrganizationResult(Organization, NotFound, Unavailable);

public readonly union SetCurrentOrganizationResult(Organization, NotFound, Unavailable);

public readonly union CreateProjectResult(Project, NotFound, Forbidden, Conflict, Unavailable);

public readonly union ListProjectsResult(ManagementPage<Project>, NotFound, Unavailable);

public readonly union GetProjectResult(Project, NotFound, Unavailable);

public readonly union RenameProjectResult(Project, NotFound, Forbidden, Conflict, Unavailable);

public readonly union DeleteProjectResult(Done, NotFound, Forbidden, Conflict, Unavailable);

public readonly union CreateDatabaseResult(ManagedDatabase, NotFound, Forbidden, Conflict, Unavailable);

public readonly union SetDatabaseRegionsResult(ManagedDatabase, NotFound, Forbidden, Conflict, Unavailable);

public readonly union FindDatabaseRecordResult(ManagedDatabase, NotFound, Unavailable);

public readonly union ListDatabasesResult(ManagementPage<ManagedDatabase>, NotFound, Unavailable);

public readonly union GetDatabaseResult(ManagedDatabase, NotFound, Unavailable);

public readonly union DatabaseTenantResult(TenantContext, NotFound, Unavailable);

public readonly union WritableDatabaseTenantResult(TenantContext, NotFound, Forbidden, Unavailable);

public readonly union WritableProjectResult(Project, NotFound, Forbidden, Unavailable);

public readonly union DeleteDatabaseResult(Done, NotFound, Forbidden, Conflict, Unavailable);

public readonly union BranchDatabaseResult(ManagedDatabase, NotFound, Forbidden, Conflict, QuotaExceeded, Unavailable);

public readonly union CopyDatabaseResult(ManagedDatabase, NotFound, Forbidden, Conflict, QuotaExceeded, Unavailable);

public sealed record ManagementPage<T>(IReadOnlyList<T> Items, string? Next);

public interface IManagementService
{
    ValueTask<WritableDatabaseTenantResult> GetRecoveryDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, string? authorityToken, CancellationToken cancellationToken);

    ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken);

    ValueTask<ListOrganizationsResult> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken);

    ValueTask<GetOrganizationResult> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    ValueTask<RenameOrganizationResult> RenameOrganizationAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken);

    ValueTask<SetPlanResult> SetPlanAsync(string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken);

    ValueTask<DeleteOrganizationResult> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    ValueTask<GetCurrentOrganizationResult> GetCurrentOrganizationAsync(string userId, CancellationToken cancellationToken);

    ValueTask<SetCurrentOrganizationResult> SetCurrentOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken);

    ValueTask<CreateProjectResult> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken);

    ValueTask<ListProjectsResult> ListProjectsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken);

    ValueTask<GetProjectResult> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    ValueTask<RenameProjectResult> RenameProjectAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken);

    ValueTask<DeleteProjectResult> DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    ValueTask<CreateDatabaseResult> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken, IReadOnlyList<string>? replicaRegions = null,
        bool allRegions = false);

    ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionsAsync(
        string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken,
        bool allRegions = false);

    ValueTask<SetDatabaseRegionsResult> RenameDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken);

    ValueTask<FindDatabaseRecordResult> FindDatabaseRecordAsync(string organizationId, string projectId, string name, CancellationToken cancellationToken);

    ValueTask<ListDatabasesResult> ListDatabasesAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken);

    ValueTask<GetDatabaseResult> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    ValueTask<DatabaseTenantResult> GetDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    ValueTask<WritableDatabaseTenantResult> GetWritableDatabaseTenantAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    ValueTask<WritableProjectResult> GetWritableProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);

    ValueTask<DeleteDatabaseResult> DeleteDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken);

    ValueTask<BranchDatabaseResult> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken);

    ValueTask<CopyDatabaseResult> CopyDatabaseAsync(
        string userId, string organizationId, string projectId, string sourceId, string name, string? branchedFrom, CancellationToken cancellationToken);
}
