using System.Net;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

/// <summary>A purpose-bound step-up request. ReturnUri is the registered dashboard origin's /auth/step-up/callback. OrganizationId is required for and binds delete_organization, start_ownership_transfer and change_plan proofs.</summary>
public sealed record TerraScaleAuthStepUpRequest(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("return_uri")] string ReturnUri,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null);

/// <summary>A pending proof. Keep Proof on the backend; navigate the browser only to StepUpUrl.</summary>
public sealed record TerraScaleAuthStepUp(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("step_up_url")] string StepUpUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <summary>Omits the bearer proof from diagnostic formatting.</summary>
    public override string ToString() => nameof(TerraScaleAuthStepUp);
}

/// <summary>Consumes an approved proof once. Account deletion additionally binds the exact deletion receipt. change_plan and delete_organization (ts-auth) require OrganizationId,
/// the organization about to change: a proof minted for another organization is refused with step_up_required and stays unconsumed. create_pat and delete_account (ts-dashboard) leave it null.</summary>
public sealed record TerraScaleAuthStepUpConsume(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("operation_receipt")] string? OperationReceipt = null,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null)
{
    /// <summary>Omits proof and deletion capabilities from diagnostic formatting.</summary>
    public override string ToString() => nameof(TerraScaleAuthStepUpConsume);
}

/// <summary>A deletion consumption acknowledgement. Keep the grant secret on the backend.</summary>
public sealed record TerraScaleAuthStepUpGrant([property: JsonPropertyName("operation_grant")] string OperationGrant)
{
    /// <summary>Omits the deletion authorization capability from diagnostic formatting.</summary>
    public override string ToString() => nameof(TerraScaleAuthStepUpGrant);
}

/// <summary>The direct deletion outcome and optional continuation capability returned with a receipt.</summary>
public sealed record TerraScaleAuthAccountDeletion(bool Completed, string? OperationGrant)
{
    /// <summary>Omits the deletion authorization capability from diagnostic formatting.</summary>
    public override string ToString() => nameof(TerraScaleAuthAccountDeletion);
}

public sealed partial class TerraScaleAuthPlatformClient
{
    /// <summary>Starts a five-minute step-up attempt. Sends once; existing authentication alone never approves it.</summary>
    public Task<AuthResult<TerraScaleAuthStepUp>> BeginStepUpAsync(string accessToken, TerraScaleAuthStepUpRequest request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/step-up",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpRequest,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUp,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 16 * 1024, cancellationToken);

    /// <summary>Consumes a proof once before side effects. Done for create_pat, change_plan and delete_organization (204),
    /// or the account-deletion operation_grant (200) to retain with the receipt. A lost/rejected response never authorizes continuation.
    /// DeleteOrganizationAsync consumes its delete_organization proof itself; never consume that proof first.</summary>
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
