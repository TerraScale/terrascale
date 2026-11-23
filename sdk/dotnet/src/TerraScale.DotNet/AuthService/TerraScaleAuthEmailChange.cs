using System.Net;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthEmailChangeRequest([property: JsonPropertyName("new_email")] string NewEmail);

public sealed record TerraScaleAuthEmailChange(
    [property: JsonPropertyName("new_email")] string NewEmail,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record TerraScaleAuthEmailChangeConfirmation([property: JsonPropertyName("code")] string Code)
{
    public override string ToString() => nameof(TerraScaleAuthEmailChangeConfirmation);
}

public sealed partial class TerraScaleAuthPlatformClient
{
    public Task<AuthResult<TerraScaleAuthEmailChange>> RequestEmailChangeAsync(string accessToken,
        TerraScaleAuthEmailChangeRequest request, string stepUpProof, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/account/email-change",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthEmailChangeRequest,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthEmailChange,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Accepted, 16 * 1024, cancellationToken, Proof(stepUpProof));

    public Task<AuthOutcome> ConfirmEmailChangeAsync(string accessToken, TerraScaleAuthEmailChangeConfirmation request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/account/email-change/confirm",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthEmailChangeConfirmation,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken);
}
