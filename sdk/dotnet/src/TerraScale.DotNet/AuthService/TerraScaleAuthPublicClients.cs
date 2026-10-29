using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthOidcClient
{
    /// <summary>Starts the shipped public CLI device flow. Open the returned verification URI and approve explicitly.
    /// No sign-in, approval, polling or email request runs in the background.</summary>
    public Task<TerraScaleAuthDeviceAuthorization> StartDeviceAuthorizationAsync(
        string clientId, string? scope = null, CancellationToken cancellationToken = default)
    {
        var form = PublicForm(clientId);
        if (scope is not null) form.Add(new("scope", scope));
        return client.SendOAuthFormAsync(InstanceRoute("/oauth/device"), form, null,
            TerraScaleAuthPublicClientJsonContext.Default.TerraScaleAuthDeviceAuthorization, cancellationToken);
    }

    /// <summary>Redeems one device code after approval. Sends once; authorization_pending and slow_down remain typed
    /// OAuth failures. Respect the original ExpiresIn and Interval; this call never starts a new authorization.</summary>
    public Task<TerraScaleAuthTokenResponse> ExchangeDeviceCodeAsync(
        string clientId, string deviceCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        var form = PublicForm(clientId);
        form.Add(new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"));
        form.Add(new("device_code", deviceCode));
        return client.SendOAuthFormAsync(TokenRoute(), form, null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    /// <summary>Redeems a public application's authorization code using its original registered redirect URI and PKCE
    /// verifier. The CLI has no code grant or callback; use its device flow instead. Sends once without a secret.</summary>
    public Task<TerraScaleAuthTokenResponse> ExchangePublicAuthorizationCodeAsync(
        string clientId, string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        var form = PublicForm(clientId);
        form.Add(new("grant_type", "authorization_code"));
        form.Add(new("code", code));
        form.Add(new("redirect_uri", redirectUri));
        form.Add(new("code_verifier", codeVerifier));
        return client.SendOAuthFormAsync(TokenRoute(), form, null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    /// <summary>Rotates a public client's refresh token without a client secret. Persist the returned token atomically;
    /// never replay the old token after success or an uncertain outcome. No automatic renewal or retry occurs.</summary>
    public Task<TerraScaleAuthTokenResponse> RefreshTokenAsync(
        string clientId, string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var form = PublicForm(clientId);
        form.Add(new("grant_type", "refresh_token"));
        form.Add(new("refresh_token", refreshToken));
        return client.SendOAuthFormAsync(TokenRoute(), form, null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    /// <summary>Revokes a public client's token/sign-in without a client secret. Unknown tokens also succeed.
    /// Existing resource tokens remain usable until their own expiry.</summary>
    public Task RevokeTokenAsync(string clientId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var form = PublicForm(clientId);
        form.Add(new("token", token));
        return client.SendOAuthFormForSuccessAsync(InstanceRoute("/oauth/revoke"), form, null, cancellationToken);
    }

    /// <summary>Exchanges this public client's sign-in token for its user's current organization/project authority.
    /// Scope is space-delimited; null requests allowed scopes. Database restriction is unsupported by this grant.</summary>
    public Task<TerraScaleAuthTokenResponse> ExchangePublicClientTokenAsync(
        string clientId, string subjectToken, string organizationId, string? projectId = null,
        string? scope = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectToken);
        TerraScaleIds.Validate(organizationId);
        if (projectId is not null) TerraScaleIds.Validate(projectId);
        var form = PublicForm(clientId);
        form.Add(new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"));
        form.Add(new("subject_token", subjectToken));
        form.Add(new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"));
        form.Add(new("organization", organizationId));
        if (projectId is not null) form.Add(new("project", projectId));
        if (scope is not null) form.Add(new("scope", scope));
        return client.SendOAuthFormAsync(TokenRoute(), form, null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    private static List<KeyValuePair<string, string>> PublicForm(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return [new("client_id", clientId)];
    }
}

/// <summary>A pending public-client device authorization. Lifetime starts when the response is received.</summary>
/// <param name="DeviceCode">Secret sent only to the token endpoint.</param>
/// <param name="UserCode">Code the account owner verifies on the hosted page.</param>
/// <param name="VerificationUri">Hosted page requiring sign-in and explicit approval.</param>
/// <param name="ExpiresIn">Original lifetime in seconds; polling never extends it.</param>
/// <param name="Interval">Minimum polling interval in seconds; honor slow_down increases.</param>
/// <param name="VerificationUriComplete">Optional hosted URI containing the user code.</param>
public sealed record TerraScaleAuthDeviceAuthorization(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval,
    [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete = null)
{
    /// <inheritdoc />
    public override string ToString() => $"TerraScaleAuthDeviceAuthorization {{ ExpiresIn = {ExpiresIn}, Interval = {Interval}, sensitive fields redacted }}";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraScaleAuthDeviceAuthorization))]
internal sealed partial class TerraScaleAuthPublicClientJsonContext : JsonSerializerContext;
