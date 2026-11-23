using System.Net;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthStepUpRequest(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("return_uri")] string ReturnUri,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null);

public sealed record TerraScaleAuthStepUp(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("step_up_url")] string StepUpUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(TerraScaleAuthStepUp);
}

public sealed record TerraScaleAuthStepUpConsume(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("operation_receipt")] string? OperationReceipt = null,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null)
{
    public override string ToString() => nameof(TerraScaleAuthStepUpConsume);
}

public sealed record TerraScaleAuthStepUpGrant([property: JsonPropertyName("operation_grant")] string OperationGrant)
{
    public override string ToString() => nameof(TerraScaleAuthStepUpGrant);
}

public sealed record TerraScaleAuthAccountDeletion(bool Completed, string? OperationGrant)
{
    public override string ToString() => nameof(TerraScaleAuthAccountDeletion);
}

public sealed partial class TerraScaleAuthPlatformClient
{
    public Task<AuthResult<TerraScaleAuthStepUp>> BeginStepUpAsync(string accessToken, TerraScaleAuthStepUpRequest request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/step-up",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpRequest,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUp,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 16 * 1024, cancellationToken);

    public async Task<StepUpConsumed> ConsumeStepUpAsync(string accessToken, TerraScaleAuthStepUpConsume request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Purpose == "delete_account")
        {
            var grant = await client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume",
                request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpConsume,
                TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpGrant,
                TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken).ConfigureAwait(false);
            return grant switch
            {
                TerraScaleAuthStepUpGrant answered => IsCapability(answered.OperationGrant)
                    ? answered
                    : TerraScaleAuthErrors.InvalidResponse("ts-auth returned an invalid deletion grant."),
                AuthRefused refused => refused,
            };
        }
        var consumed = await client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume",
            request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpConsume,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken).ConfigureAwait(false);
        return consumed switch
        {
            Done done => done,
            AuthRefused refused => refused,
        };
    }

    internal static bool IsCapability(string? value) => value is { Length: > 0 and <= 8192 }
        && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == ',');

    internal static string DeletionAuthorization(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsCapability(value)) throw new ArgumentException("A deletion authorization must be a nonempty capability without whitespace or control characters, at most 8192 characters.", nameof(value));
        return value;
    }
}
