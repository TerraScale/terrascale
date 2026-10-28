using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>The new sign-in address to verify.</summary>
public sealed record TerraScaleAuthEmailChangeRequest([property: JsonPropertyName("new_email")] string NewEmail);

/// <summary>A pending change. The new address receives a six-digit code valid for ten minutes.</summary>
public sealed record TerraScaleAuthEmailChange(
    [property: JsonPropertyName("new_email")] string NewEmail,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>The six-digit code sent to the new address.</summary>
public sealed record TerraScaleAuthEmailChangeConfirmation([property: JsonPropertyName("code")] string Code)
{
    /// <summary>Omits the verification code from diagnostic formatting.</summary>
    public override string ToString() => nameof(TerraScaleAuthEmailChangeConfirmation);
}

public sealed partial class TerraScaleAuthPlatformClient
{
    /// <summary>
    /// Requests a sign-in email change with an approved change_email/ts-auth proof. Sends once and replaces
    /// any pending change. Throws email_taken, step_up_required, invalid_request or rate_limited on refusal.
    /// Keep the proof server-side; never automatically retry an uncertain response.
    /// </summary>
    public Task<TerraScaleAuthEmailChange> RequestEmailChangeAsync(string accessToken,
        TerraScaleAuthEmailChangeRequest request, string stepUpProof, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/account/email-change",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthEmailChangeRequest,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthEmailChange,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Accepted, 16 * 1024, cancellationToken, Proof(stepUpProof));

    /// <summary>
    /// Confirms the pending change from the same account authentication state. Sends once. Success ends every
    /// session and sign-in token, including accessToken: discard local sessions and sign in with the new address.
    /// Expired, canceled, replaced or invalid codes fail with email_change_denied; conflicts fail with email_taken.
    /// </summary>
    public Task ConfirmEmailChangeAsync(string accessToken, TerraScaleAuthEmailChangeConfirmation request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/account/email-change/confirm",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthEmailChangeConfirmation,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken);
}
