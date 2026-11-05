using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private static string PublishedPlanKey(string organizationId) => State(organizationId) + "/plan";

    private static async ValueTask<bool> IsPlanPublishedAsync(IStorageTransaction transaction, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Catalog, PublishedPlanKey(organizationId), Id, cancellationToken).ConfigureAwait(false) == PlanName(plan);

    private static ValueTask PublishPlanAsync(IStorageTransaction transaction, string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        PutAsync(transaction, Catalog, PublishedPlanKey(organizationId), PlanName(plan), Id, cancellationToken);

    private async ValueTask<TenantPlanResult> AdmissionPlanAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken)
    {
        var organization = await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
        if (organization is { Plan: not PlanTier.Hobby } or { CreatedBy: not OperatorHandler.KeyId }
            || await IsPlanPublishedAsync(transaction, organizationId, PlanTier.Hobby, cancellationToken).ConfigureAwait(false)
            || (await regionPeers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return organization?.Plan ?? PlanTier.Hobby;
        }

        return new Unavailable("the organization's plan has not been published in this region; retry shortly", "plan_unknown");
    }
}
