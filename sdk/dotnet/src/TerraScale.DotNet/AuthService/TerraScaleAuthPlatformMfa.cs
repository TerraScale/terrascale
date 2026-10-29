using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Replaces the organization's MFA policy. Both true and false must be supplied explicitly; false removes the requirement.</summary>
/// <param name="RequireMfa">Whether members must have an active second factor.</param>
public sealed record TerraScaleAuthPlatformMfaPolicy([property: JsonPropertyName("require_mfa")] bool RequireMfa);

/// <summary>The signed-in caller's second-factor status and hosted management page.</summary>
/// <param name="Enabled">Whether an authenticator app is active.</param>
/// <param name="RecoveryCodesRemaining">Unused, unexpired recovery codes (0 to 8).</param>
/// <param name="ManageUrl">The producer's hosted security page; reading status does not navigate to it.</param>
public sealed record TerraScaleAuthPlatformAccountMfa(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("recovery_codes_remaining")] int RecoveryCodesRemaining,
    [property: JsonPropertyName("manage_url")] string ManageUrl);

public sealed partial class TerraScaleAuthPlatformClient
{
    /// <summary>get_account_mfa: GET /v1/terrascale/account/mfa. Reads the sign-in caller's own status once.</summary>
    public Task<TerraScaleAuthPlatformAccountMfa> GetAccountMfaAsync(string accessToken, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, "/v1/terrascale/account/mfa",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAccountMfa,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    /// <summary>set_organization_mfa_policy: PUT …/organizations/{organization}/mfa-policy. Requires owner; enabling needs the caller's active factor (409 mfa_not_enrolled). Sent once; no enrollment or retries.</summary>
    public Task<TerraScaleAuthPlatformOrganization> SetOrganizationMfaPolicyAsync(string accessToken, string organizationId,
        TerraScaleAuthPlatformMfaPolicy request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/mfa-policy",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMfaPolicy,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);
}
