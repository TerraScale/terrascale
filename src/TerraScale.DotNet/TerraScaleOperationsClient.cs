using System.Globalization;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Authoritative durable provisioning progress.</summary>
public sealed class TerraScaleOperationsClient(TerraScaleClient client)
{
    /// <summary>Lists the durable operations visible in the configured project.</summary>
    /// <param name="limit">Maximum number of records to return, from 1 through 50.</param>
    /// <param name="after">Opaque cursor returned by the preceding page.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    public Task<OperationPage> ListAsync(int limit = 50, string? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 50.");
        }

        var route = "/api/v1/operations?";
        if (after is not null)
        {
            route += "after=" + TerraScaleManagementPaths.Escape(after) + "&";
        }

        route += "limit=" + limit.ToString(CultureInfo.InvariantCulture);
        return client.SendJsonAsync(HttpMethod.Get, route,
            TerraScaleContractJsonContext.Default.OperationPage, cancellationToken);
    }

    public Task<ProvisioningOperation> GetAsync(string operationId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, "/api/v1/operations/" + TerraScaleManagementPaths.Escape(operationId),
            TerraScaleContractJsonContext.Default.ProvisioningOperation, cancellationToken);
}
