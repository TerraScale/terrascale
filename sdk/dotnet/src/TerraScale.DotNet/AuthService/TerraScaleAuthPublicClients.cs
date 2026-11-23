using System.Text.Json;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthOidcClient
{
    public Task<AuthResult<TerraScaleAuthDeviceAuthorization>> StartDeviceAuthorizationAsync(
        string clientId, string? scope = null, CancellationToken cancellationToken = default)
    {
        var form = PublicForm(clientId);
        if (scope is not null) form.Add(new("scope", scope));
        return client.SendOAuthFormAsync(InstanceRoute("/oauth/device"), form, null,
            TerraScaleAuthPublicClientJsonContext.Default.TerraScaleAuthDeviceAuthorization, cancellationToken);
    }

    public Task<AuthResult<TerraScaleAuthTokenResponse>> ExchangeDeviceCodeAsync(
        string clientId, string deviceCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        var form = PublicForm(clientId);
        form.Add(new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"));
        form.Add(new("device_code", deviceCode));
        return client.SendOAuthFormAsync(TokenRoute(), form, null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    public Task<AuthResult<TerraScaleAuthTokenResponse>> ExchangePublicAuthorizationCodeAsync(
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

    public Task<AuthResult<TerraScaleAuthTokenResponse>> RefreshTokenAsync(
        string clientId, string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var form = PublicForm(clientId);
        form.Add(new("grant_type", "refresh_token"));
        form.Add(new("refresh_token", refreshToken));
        return client.SendOAuthFormAsync(TokenRoute(), form, null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    public Task<AuthOutcome> RevokeTokenAsync(string clientId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var form = PublicForm(clientId);
        form.Add(new("token", token));
        return client.SendOAuthFormForSuccessAsync(InstanceRoute("/oauth/revoke"), form, null, cancellationToken);
    }

    public Task<AuthResult<TerraScaleAuthTokenResponse>> ExchangePublicClientTokenAsync(
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

public sealed record TerraScaleAuthDeviceAuthorization(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval,
    [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete = null)
{
    public override string ToString() => $"TerraScaleAuthDeviceAuthorization {{ ExpiresIn = {ExpiresIn}, Interval = {Interval}, sensitive fields redacted }}";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraScaleAuthDeviceAuthorization))]
internal sealed partial class TerraScaleAuthPublicClientJsonContext : JsonSerializerContext;
