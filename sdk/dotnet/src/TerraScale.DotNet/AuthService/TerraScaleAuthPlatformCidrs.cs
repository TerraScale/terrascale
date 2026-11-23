using System.Net;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthPlatformAllowedCidrs([property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs);

public sealed partial class TerraScaleAuthPlatformClient
{
    public Task<AuthResult<TerraScaleAuthPlatformProject>> SetProjectAllowedCidrsAsync(string accessToken, string organizationId, string projectId,
        TerraScaleAuthPlatformAllowedCidrs request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/allowed-cidrs",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformApiKey>> SetProjectApiKeyAllowedCidrsAsync(string accessToken, string organizationId, string projectId, string keyId,
        TerraScaleAuthPlatformAllowedCidrs request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}/allowed-cidrs",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformApiKey>> SetOrganizationApiKeyAllowedCidrsAsync(string accessToken, string organizationId, string keyId,
        TerraScaleAuthPlatformAllowedCidrs request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}/allowed-cidrs",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);
}
