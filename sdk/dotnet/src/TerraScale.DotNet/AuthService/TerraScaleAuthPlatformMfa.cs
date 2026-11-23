using System.Net;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthPlatformMfaPolicy([property: JsonPropertyName("require_mfa")] bool RequireMfa);

public sealed record TerraScaleAuthPlatformAccountMfa(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("recovery_codes_remaining")] int RecoveryCodesRemaining,
    [property: JsonPropertyName("manage_url")] string ManageUrl);

public sealed partial class TerraScaleAuthPlatformClient
{
    public Task<AuthResult<TerraScaleAuthPlatformAccountMfa>> GetAccountMfaAsync(string accessToken, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, "/v1/terrascale/account/mfa",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAccountMfa,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformOrganization>> SetOrganizationMfaPolicyAsync(string accessToken, string organizationId,
        TerraScaleAuthPlatformMfaPolicy request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/mfa-policy",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMfaPolicy,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);
}
