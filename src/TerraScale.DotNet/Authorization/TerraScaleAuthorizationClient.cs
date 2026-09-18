using TerraScale.DotNet.Generated;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>
/// Anonymous authorization lease lookup for previously authenticated producers.
/// </summary>
public sealed class TerraScaleAuthorizationClient(TerraScaleClient client)
{
    /// <summary>
    /// Returns a previously authenticated producer lease for a tenant, with its
    /// original absolute expiry. The route never fetches identity state or
    /// renews a lease on a reader's behalf.
    /// </summary>
    public Task<Lease> GetLeaseAsync(string tenant, CancellationToken cancellationToken = default) =>
        client.SendAnonymousJsonAsync(
            HttpMethod.Get,
            "/api/v1/authorization/leases/" + TerraScaleManagementPaths.Escape(tenant),
            TerraScaleContractJsonContext.Default.Lease,
            cancellationToken);
}
