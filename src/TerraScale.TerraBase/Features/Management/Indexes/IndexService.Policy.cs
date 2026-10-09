using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Indexes;

internal sealed partial class IndexService
{
    // Only opt-outs are stored: both the count and bytes per record (a validated collection name) are bounded.
    internal const int MaxAutoIndexOptOuts = 1000;

    public ValueTask<GetAutoIndexPolicyResult> GetAutoIndexPolicyAsync(
        string userId, TenantContext database, string collection, CancellationToken cancellationToken) =>
        catalog.ReadAsync<GetAutoIndexPolicyResult>(async transaction =>
        {
            if (await EngineManagementService.GetDatabaseAsync(
                transaction, userId, database.OrganizationId, database.ProjectId!, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            return await GetAsync(transaction, AutoIndexPolicies(database.DatabaseId!), collection, TerraBaseJsonContext.Default.AutoIndexPolicy, cancellationToken).ConfigureAwait(false)
                ?? new AutoIndexPolicy(collection, true);
        }, static error => error, cancellationToken);

    public ValueTask<SetAutoIndexPolicyResult> SetAutoIndexPolicyAsync(
        string userId, TenantContext database, string collection, bool enabled, CancellationToken cancellationToken) =>
        catalog.WriteAsync<SetAutoIndexPolicyResult>(async transaction =>
        {
            if (await EngineManagementService.GetDatabaseAsync(
                transaction, userId, database.OrganizationId, database.ProjectId!, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var access = await EngineManagementService.WritableAsync(transaction, userId, database.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    Forbidden => ManagementErrors.RoleTooLow,
                    NotFound => new NotFound(),
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            var records = AutoIndexPolicies(database.DatabaseId!);
            var existing = await GetAsync(transaction, records, collection, TerraBaseJsonContext.Default.AutoIndexPolicy, cancellationToken).ConfigureAwait(false);
            var policy = new AutoIndexPolicy(collection, enabled);
            if (enabled)
            {
                if (existing is not null)
                {
                    await transaction.DeleteAsync(records, collection, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (existing is null)
            {
                if (await transaction.CountDocumentsAsync(records, cancellationToken).ConfigureAwait(false) >= MaxAutoIndexOptOuts)
                {
                    return new Validation($"a database may opt out at most {MaxAutoIndexOptOuts} collections", "auto_index_policy_limit");
                }

                await PutAsync(transaction, records, collection, policy, TerraBaseJsonContext.Default.AutoIndexPolicy, cancellationToken).ConfigureAwait(false);
            }

            if ((existing is null) != enabled)
            {
                await EngineManagementService.TouchOrganizationAsync(transaction, database.OrganizationId, cancellationToken).ConfigureAwait(false);
            }

            return policy;
        }, static error => error, cancellationToken);

    private static string AutoIndexPolicies(string databaseId) => DatabaseState(databaseId) + "auto_index_policies";
}
