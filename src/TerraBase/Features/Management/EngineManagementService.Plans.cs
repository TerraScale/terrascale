using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private static string PublishedPlanKey(string organizationId) => State(organizationId) + "/plan";

    private static async ValueTask<bool> IsPlanPublishedAsync(IStorageTransaction transaction, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Catalog, PublishedPlanKey(organizationId), Id, cancellationToken).ConfigureAwait(false) == PlanName(plan);

    private static ValueTask PublishPlanAsync(IStorageTransaction transaction, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        PutAsync(transaction, Catalog, PublishedPlanKey(organizationId), PlanName(plan), Id, cancellationToken);

    private async ValueTask<TenantPlanResult> AdmissionPlanAsync(IStorageTransaction transaction, string organizationId, bool writer, CancellationToken cancellationToken)
    {
        var organization = await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
        if (!writer || organization is { Plan: not PlanTier.Hobby } or { CreatedBy: not OperatorHandler.KeyId }
            || await IsPlanPublishedAsync(transaction, organizationId, PlanTier.Hobby, cancellationToken).ConfigureAwait(false)
            || options.PeersFile is null && (await regionPeers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return organization?.Plan ?? PlanTier.Hobby;
        }

        return new Unavailable("the organization's plan has not been published in this region; retry shortly", "plan_unknown");
    }

    public async ValueTask<SetPlanResult> SetPlanAsync(
        string userId, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        PlatformDatabases.IsPlatform(organizationId) ? ManagementErrors.PlatformPlanLocked : await WriteAsync<SetPlanResult>(async transaction =>
        {
            var access = await AuthorizeAsync(transaction, userId, organizationId, allowProject: false, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("an organization index names a missing organization");
            organization = organization with { Plan = plan };
            _ = await CatalogTables.PutOrganizationAsync(transaction, organization, cancellationToken).ConfigureAwait(false);

            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

}
