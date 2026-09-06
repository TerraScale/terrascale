using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Authoritative durable provisioning progress.</summary>
public sealed class TerraScaleOperationsClient(TerraScaleClient client)
{
    public Task<ProvisioningOperation> GetAsync(string operationId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, "/api/v1/operations/" + TerraScaleManagementPaths.Escape(operationId),
            TerraScaleContractJsonContext.Default.ProvisioningOperation, cancellationToken);
}
