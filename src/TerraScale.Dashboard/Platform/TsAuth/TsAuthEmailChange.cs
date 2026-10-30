using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed record TsAuthEmailChangeRequest([property: JsonPropertyName("new_email")] string NewEmail);

    public sealed record TsAuthEmailChange(
        [property: JsonPropertyName("new_email")] string NewEmail,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    public sealed record TsAuthEmailChangeConfirmation([property: JsonPropertyName("code")] string Code)
    {
        public override string ToString()
        {
            return nameof(TsAuthEmailChangeConfirmation);
        }
    }

    public sealed partial class TsAuthPlatformClient
    {
        public Task<TsAuthEmailChange> RequestEmailChangeAsync(string accessToken,
            TsAuthEmailChangeRequest request, string stepUpProof, CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/account/email-change",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthEmailChangeRequest,
                TsAuthIntegrationJsonContext.Default.TsAuthEmailChange,
                TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Accepted, 16 * 1024, cancellationToken, Proof(stepUpProof));
        }

        public Task ConfirmEmailChangeAsync(string accessToken, TsAuthEmailChangeConfirmation request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/account/email-change/confirm",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthEmailChangeConfirmation,
                TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken);
        }
    }
}
