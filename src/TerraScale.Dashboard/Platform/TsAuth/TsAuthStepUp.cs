using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed record TsAuthStepUpRequest(
        [property: JsonPropertyName("purpose")] string Purpose,
        [property: JsonPropertyName("audience")] string Audience,
        [property: JsonPropertyName("return_uri")] string ReturnUri);

    public sealed record TsAuthStepUp(
        [property: JsonPropertyName("proof")] string Proof,
        [property: JsonPropertyName("step_up_url")] string StepUpUrl,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
    {
        public override string ToString()
        {
            return nameof(TsAuthStepUp);
        }
    }

    public sealed record TsAuthStepUpConsume(
        [property: JsonPropertyName("proof")] string Proof,
        [property: JsonPropertyName("purpose")] string Purpose,
        [property: JsonPropertyName("audience")] string Audience,
        [property: JsonPropertyName("operation_receipt")] string? OperationReceipt = null)
    {
        public override string ToString()
        {
            return nameof(TsAuthStepUpConsume);
        }
    }

    public sealed record TsAuthStepUpGrant([property: JsonPropertyName("operation_grant")] string OperationGrant)
    {
        public override string ToString()
        {
            return nameof(TsAuthStepUpGrant);
        }
    }

    public sealed partial class TsAuthPlatformClient
    {
        public Task<TsAuthStepUp> BeginStepUpAsync(string accessToken, TsAuthStepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/step-up",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthStepUpRequest,
                TsAuthIntegrationJsonContext.Default.TsAuthStepUp,
                TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 16 * 1024, cancellationToken);
        }

        public async Task<string?> ConsumeStepUpAsync(string accessToken, TsAuthStepUpConsume request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Purpose == "delete_account")
            {
                var grant = await client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume",
                    request, TsAuthIntegrationJsonContext.Default.TsAuthStepUpConsume,
                    TsAuthIntegrationJsonContext.Default.TsAuthStepUpGrant,
                    TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken).ConfigureAwait(false);
                if (!IsCapability(grant.OperationGrant))
                    throw new TsAuthException(HttpStatusCode.OK, "ts-auth returned an invalid deletion grant.", "invalid_response", null);
                return grant.OperationGrant;
            }
            await client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume",
                request, TsAuthIntegrationJsonContext.Default.TsAuthStepUpConsume,
                TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken).ConfigureAwait(false);
            return null;
        }

        internal static bool IsCapability(string? value)
        {
            return value is { Length: > 0 and <= 8192 }
            && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == ',');
        }

        internal static string DeletionAuthorization(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsCapability(value)) throw new ArgumentException("A deletion authorization must be a nonempty capability without whitespace or control characters, at most 8192 characters.", nameof(value));
            return value;
        }
    }
}
