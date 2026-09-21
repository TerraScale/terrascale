namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Auth.Templates;

internal static class OrganizationDomainSupport
{
    // ---- Helpers ---------------------------------------------------------------------

    internal static async Task<Organization?> FindOrganizationAsync(AuthStore store, string instanceId, string orgId, CancellationToken ct)
    {
        var organization = await store.GetOrganizationAsync(orgId, ct);
        return organization is not null && organization.InstanceId == instanceId ? organization : null;
    }

    internal static object Render(OrgDomain domain) =>
        new { id = domain.Id, organization_id = domain.OrgId, domain = domain.Domain, created_at = domain.CreatedAt };
}
