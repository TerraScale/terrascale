using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Backup;

/// <summary>Holds one <see cref="IManagementService.CopyDatabaseAsync"/> call — the restore copy — until released.</summary>
internal sealed class CopyGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public void Decorate(IServiceCollection services)
    {
        var registered = services.Last(candidate => candidate.ServiceType == typeof(IManagementService));
        services.Replace(ServiceDescriptor.Describe(
            typeof(IManagementService),
            provider => new GatedManagement((IManagementService)registered.ImplementationFactory!.Invoke(provider), this),
            registered.Lifetime));
    }

    private sealed class GatedManagement(IManagementService inner, CopyGate gate) : IManagementService
    {
        public ValueTask<RestorePrecheckResult> PrecheckRestoreAsync(
            string userId, string organizationId, string projectId, string? name, CancellationToken cancellationToken) =>
            inner.PrecheckRestoreAsync(userId, organizationId, projectId, name, cancellationToken);

        public ValueTask<WritableDatabaseTenantResult> GetRecoveryDatabaseTenantAsync(
            string userId, string organizationId, string projectId, string databaseId, string? authorityToken, CancellationToken cancellationToken) =>
            inner.GetRecoveryDatabaseTenantAsync(userId, organizationId, projectId, databaseId, authorityToken, cancellationToken);

        public ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken) =>
            inner.CreateOrganizationAsync(userId, name, cancellationToken);

        public ValueTask<ListOrganizationsResult> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken) =>
            inner.ListOrganizationsAsync(userId, after, limit, cancellationToken);

        public ValueTask<GetOrganizationResult> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
            inner.GetOrganizationAsync(userId, organizationId, cancellationToken);

        public ValueTask<SetPlanResult> SetPlanAsync(string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
            inner.SetPlanAsync(userId, organizationId, plan, cancellationToken);

        public ValueTask<DeleteOrganizationResult> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
            inner.DeleteOrganizationAsync(userId, organizationId, cancellationToken);

        public ValueTask<CreateProjectResult> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken) =>
            inner.CreateProjectAsync(userId, organizationId, name, cancellationToken);

        public ValueTask<ListProjectsResult> ListProjectsAsync(
            string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
            inner.ListProjectsAsync(userId, organizationId, after, limit, cancellationToken);

        public ValueTask<GetProjectResult> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
            inner.GetProjectAsync(userId, organizationId, projectId, cancellationToken);

        public ValueTask<DeleteProjectResult> DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
            inner.DeleteProjectAsync(userId, organizationId, projectId, cancellationToken);

        public ValueTask<CreateDatabaseResult> CreateDatabaseAsync(
            string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken, IReadOnlyList<string>? replicaRegions = null,
            bool allRegions = false, long? ttlSeconds = null) =>
            inner.CreateDatabaseAsync(userId, organizationId, projectId, name, cancellationToken, replicaRegions, allRegions, ttlSeconds);

        public ValueTask<SetDatabaseRegionsResult> SetDatabaseTtlAsync(
            string userId, string organizationId, string projectId, string databaseId, long? ttlSeconds, CancellationToken cancellationToken) =>
            inner.SetDatabaseTtlAsync(userId, organizationId, projectId, databaseId, ttlSeconds, cancellationToken);

        public ValueTask<SetDatabaseRegionsResult> SetDatabaseRegionsAsync(
            string userId, string organizationId, string projectId, string databaseId, IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken,
            bool allRegions = false) =>
            inner.SetDatabaseRegionsAsync(userId, organizationId, projectId, databaseId, replicaRegions, cancellationToken, allRegions);

        public ValueTask<SetDatabaseRegionsResult> RenameDatabaseAsync(
            string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken) =>
            inner.RenameDatabaseAsync(userId, organizationId, projectId, databaseId, name, cancellationToken);

        public ValueTask<FindDatabaseRecordResult> FindDatabaseRecordAsync(string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
            inner.FindDatabaseRecordAsync(organizationId, projectId, name, cancellationToken);

        public ValueTask<ListProjectDatabasesResult> ListProjectDatabasesAsync(
            string projectId, string? after, int limit, CancellationToken cancellationToken) =>
            inner.ListProjectDatabasesAsync(projectId, after, limit, cancellationToken);

        public ValueTask<ListDatabasesResult> ListDatabasesAsync(
            string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken) =>
            inner.ListDatabasesAsync(userId, organizationId, projectId, after, limit, cancellationToken);

        public ValueTask<GetDatabaseResult> GetDatabaseAsync(
            string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
            inner.GetDatabaseAsync(userId, organizationId, projectId, databaseId, cancellationToken);

        public ValueTask<DatabaseTenantResult> GetDatabaseTenantAsync(
            string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
            inner.GetDatabaseTenantAsync(userId, organizationId, projectId, databaseId, cancellationToken);

        public ValueTask<WritableDatabaseTenantResult> GetWritableDatabaseTenantAsync(
            string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
            inner.GetWritableDatabaseTenantAsync(userId, organizationId, projectId, databaseId, cancellationToken);

        public ValueTask<WritableProjectResult> GetWritableProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
            inner.GetWritableProjectAsync(userId, organizationId, projectId, cancellationToken);

        public ValueTask<DeleteDatabaseResult> DeleteDatabaseAsync(
            string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
            inner.DeleteDatabaseAsync(userId, organizationId, projectId, databaseId, cancellationToken);

        public ValueTask<BranchDatabaseResult> BranchDatabaseAsync(
            string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken, long? ttlSeconds = null) =>
            inner.BranchDatabaseAsync(userId, organizationId, projectId, databaseId, name, cancellationToken, ttlSeconds);

        public async ValueTask<CopyDatabaseResult> CopyDatabaseAsync(
            string userId, string organizationId, string projectId, string sourceId, string name, string? branchedFrom, CancellationToken cancellationToken, long? ttlSeconds = null)
        {
            gate._entered.TrySetResult();
            await gate._release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.CopyDatabaseAsync(userId, organizationId, projectId, sourceId, name, branchedFrom, cancellationToken, ttlSeconds).ConfigureAwait(false);
        }
    }
}
