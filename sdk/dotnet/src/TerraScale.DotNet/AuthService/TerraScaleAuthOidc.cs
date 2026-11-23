using System.Text.Json;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthOidcClient(TerraScaleAuthClient client)
{
    public Task<AuthResult<TerraScaleAuthOidcDiscovery>> GetDiscoveryDocumentAsync(CancellationToken cancellationToken = default) =>
        client.GetMetadataAsync(
            InstanceRoute("/.well-known/openid-configuration"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthOidcDiscovery,
            cancellationToken);

    public Task<AuthResult<TerraScaleAuthJwkSet>> GetJwksAsync(CancellationToken cancellationToken = default) =>
        client.GetMetadataAsync(
            InstanceRoute("/.well-known/jwks.json"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthJwkSet,
            cancellationToken);

    public Task<AuthResult<TerraScaleAuthTokenResponse>> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        string? codeVerifier = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("code", code),
            new("redirect_uri", redirectUri),
        };
        if (codeVerifier is not null)
        {
            form.Add(new("code_verifier", codeVerifier));
        }

        return client.SendOAuthFormAsync(
            TokenRoute(),
            form,
            authorization: null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse,
            cancellationToken);
    }

    public Task<AuthResult<TerraScaleAuthTokenResponse>> RequestClientCredentialsTokenAsync(
        string clientId,
        string clientSecret,
        string? scope = null,
        string? project = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        if (project is not null) TerraScaleIds.Validate(project);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", clientId),
            new("client_secret", clientSecret),
        };
        if (scope is not null)
        {
            form.Add(new("scope", scope));
        }

        if (project is not null) form.Add(new("project", project));
        return client.SendOAuthFormAsync(
            TokenRoute(),
            form,
            authorization: null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse,
            cancellationToken);
    }

    public Task<AuthResult<TerraScaleAuthTokenResponse>> RefreshTokenAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return client.SendOAuthFormAsync(
            TokenRoute(),
            [
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new("client_id", clientId),
                new("client_secret", clientSecret),
                new("refresh_token", refreshToken),
            ],
            authorization: null,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse,
            cancellationToken);
    }

    public Task<AuthOutcome> RevokeTokenAsync(string clientId, string clientSecret, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return client.SendOAuthFormForSuccessAsync(
            InstanceRoute("/oauth/revoke"),
            [
                new KeyValuePair<string, string>("client_id", clientId),
                new("client_secret", clientSecret),
                new("token", token),
            ],
            authorization: null,
            cancellationToken);
    }

    public Task<AuthResult<TerraScaleAuthUserInfo>> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default) =>
        client.GetWithBearerAsync(
            InstanceRoute("/oauth/userinfo"),
            accessToken,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUserInfo,
            cancellationToken);

    public Task<AuthResult<TerraScaleAuthTokenResponse>> ExchangeTokenAsync(
        string clientId, string clientSecret, string subjectToken, string organizationId,
        string? projectId = null, string? scope = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectToken);
        TerraScaleIds.Validate(organizationId);
        if (projectId is not null) TerraScaleIds.Validate(projectId);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
            new("subject_token", subjectToken),
            new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
            new("organization", organizationId),
        };
        if (projectId is not null) form.Add(new("project", projectId));
        if (scope is not null) form.Add(new("scope", scope));
        return client.SendOAuthFormAsync(TokenRoute(), form, TerraScaleAuthCredentials.Basic(clientId, clientSecret),
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    private string InstanceRoute(string relative) =>
        "/" + TerraScaleAuthPaths.Escape(client.InstanceSlug) + relative;

    private string TokenRoute() => InstanceRoute("/oauth/token");
}

public sealed record TerraScaleAuthOidcDiscovery(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string UserinfoEndpoint,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("revocation_endpoint")] string RevocationEndpoint,
    [property: JsonPropertyName("introspection_endpoint")] string? IntrospectionEndpoint,
    [property: JsonPropertyName("end_session_endpoint")] string EndSessionEndpoint,
    [property: JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypesSupported,
    [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethodsSupported,
    [property: JsonPropertyName("subject_types_supported")] IReadOnlyList<string> SubjectTypesSupported,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> IdTokenSigningAlgValuesSupported,
    [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported);

public sealed record TerraScaleAuthJwkSet(
    [property: JsonPropertyName("keys")] IReadOnlyList<TerraScaleAuthJwk> Keys);

public sealed record TerraScaleAuthJwk(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("crv")] string Curve,
    [property: JsonPropertyName("alg")] string? Algorithm,
    [property: JsonPropertyName("use")] string? Use,
    [property: JsonPropertyName("kid")] string KeyId,
    [property: JsonPropertyName("x")] string X,
    [property: JsonPropertyName("y")] string Y);

public sealed record TerraScaleAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    [property: JsonPropertyName("id_token")] string? IdToken = null,
    [property: JsonPropertyName("issued_token_type")] string? IssuedTokenType = null)
{
    public override string ToString() =>
        $"{typeof(TerraScaleAuthTokenResponse).Name} {{ TokenType = {TokenType}, ExpiresIn = {ExpiresIn}, Scope = {Scope}, AccessToken = [REDACTED], RefreshToken = [REDACTED], IdToken = [REDACTED] }}";
}

public sealed record TerraScaleAuthUserInfo(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("email_verified")] bool? EmailVerified,
    [property: JsonPropertyName("name")] string? Name)
{
    [JsonPropertyName("given_name")]
    public string? GivenName { get; init; }

    [JsonPropertyName("family_name")]
    public string? FamilyName { get; init; }

    [JsonPropertyName("picture")]
    public string? Picture { get; init; }

    [JsonPropertyName("public_metadata")]
    public JsonElement? PublicMetadata { get; init; }
}
