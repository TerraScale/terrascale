using TerraScale.Dashboard.Features.Organization.Models;

namespace TerraScale.Dashboard.Shared.State;

public sealed partial class DashboardStore
{
    /// <summary>
    /// Locates one organization across every account world. A provider webhook
    /// carries no dashboard session, so it cannot rely on request activation;
    /// the sandbox demo keeps one warm process, which makes the account map the
    /// authoritative index (see the single-machine note on the class).
    /// </summary>
    public OrganizationWorld? FindOrganization(string organizationId)
    {
        if (organizationId.Length == 0)
        {
            return null;
        }

        foreach (var world in _accounts.Values)
        {
            if (world.Organizations.TryGetValue(organizationId, out var organization))
            {
                return organization;
            }
        }

        return _demoWorld.Organizations.TryGetValue(organizationId, out var demoOrganization) ? demoOrganization : null;
    }

    /// <summary>Resolves the organization billed to a Stripe customer id.</summary>
    public OrganizationWorld? FindOrganizationByCustomer(string customerId) =>
        FindOrganization(organization => organization.StripeCustomerId == customerId);

    /// <summary>Resolves the organization owning a Stripe subscription id.</summary>
    public OrganizationWorld? FindOrganizationBySubscription(string subscriptionId) =>
        FindOrganization(organization => organization.StripeSubscriptionId == subscriptionId);

    private OrganizationWorld? FindOrganization(Func<OrganizationWorld, bool> predicate)
    {
        foreach (var world in _accounts.Values)
        {
            foreach (var organization in world.Organizations.Values)
            {
                if (predicate(organization))
                {
                    return organization;
                }
            }
        }

        return _demoWorld.Organizations.Values.FirstOrDefault(predicate);
    }
}
