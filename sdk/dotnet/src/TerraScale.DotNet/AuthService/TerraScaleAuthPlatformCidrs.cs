using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Replaces an IP allowlist. The producer rejects null; an empty list clears restrictions.</summary>
/// <param name="AllowedCidrs">Up to 32 distinct normalized IPv4/IPv6 CIDRs or addresses; the producer validates and canonicalizes.</param>
public sealed record TerraScaleAuthPlatformAllowedCidrs([property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs);

public sealed partial class TerraScaleAuthPlatformClient
{
    /// <summary>set_project_allowed_cidrs: PUT …/projects/{project}/allowed-cidrs. Requires project admin; sent once. New tokens use the replacement within 60 seconds.</summary>
    public Task<TerraScaleAuthPlatformProject> SetProjectAllowedCidrsAsync(string accessToken, string organizationId, string projectId,
        TerraScaleAuthPlatformAllowedCidrs request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/allowed-cidrs",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    /// <summary>set_project_api_key_allowed_cidrs: PUT …/projects/{project}/api-keys/{key}/allowed-cidrs. Requires project member; sent once. Project restrictions also apply.</summary>
    public Task<TerraScaleAuthPlatformApiKey> SetProjectApiKeyAllowedCidrsAsync(string accessToken, string organizationId, string projectId, string keyId,
        TerraScaleAuthPlatformAllowedCidrs request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}/allowed-cidrs",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    /// <summary>set_organization_api_key_allowed_cidrs: PUT …/api-keys/{key}/allowed-cidrs. Requires organization admin; sent once. New tokens use the replacement within 60 seconds.</summary>
    public Task<TerraScaleAuthPlatformApiKey> SetOrganizationApiKeyAllowedCidrsAsync(string accessToken, string organizationId, string keyId,
        TerraScaleAuthPlatformAllowedCidrs request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}/allowed-cidrs",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);
}
