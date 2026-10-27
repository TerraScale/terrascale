using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>A purpose-bound step-up request. ReturnUri is the registered dashboard origin's /auth/step-up/callback.</summary>
public sealed record TerraScaleAuthStepUpRequest(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("return_uri")] string ReturnUri);

/// <summary>A pending proof. Keep Proof on the backend; navigate the browser only to StepUpUrl.</summary>
public sealed record TerraScaleAuthStepUp(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("step_up_url")] string StepUpUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <summary>Omits the bearer proof from diagnostic formatting.</summary>
    public override string ToString() => nameof(TerraScaleAuthStepUp);
}

/// <summary>Consumes an approved proof once. Account deletion additionally binds the exact deletion receipt.</summary>
public sealed record TerraScaleAuthStepUpConsume(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("operation_receipt")] string? OperationReceipt = null)
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
    public Task<TerraScaleAuthStepUp> BeginStepUpAsync(string accessToken, TerraScaleAuthStepUpRequest request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/step-up",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpRequest,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUp,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 16 * 1024, cancellationToken);

    /// <summary>Consumes a dashboard proof once before side effects. Returns null for PAT (204), or the deletion
    /// operation_grant (200) to retain with the receipt. A lost/rejected response never authorizes continuation.</summary>
    public async Task<string?> ConsumeStepUpAsync(string accessToken, TerraScaleAuthStepUpConsume request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Purpose == "delete_account")
        {
            var grant = await client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume",
                request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpConsume,
                TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpGrant,
                TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken).ConfigureAwait(false);
            if (!IsCapability(grant.OperationGrant))
                throw new TerraScaleAuthException(HttpStatusCode.OK, "ts-auth returned an invalid deletion grant.", "invalid_response", null);
            return grant.OperationGrant;
        }
        await client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume",
            request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthStepUpConsume,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken).ConfigureAwait(false);
        return null;
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
