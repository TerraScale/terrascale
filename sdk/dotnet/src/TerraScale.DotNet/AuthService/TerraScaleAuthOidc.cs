using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// The instance's public OAuth 2.1/OIDC endpoints: discovery metadata, JWKS,
/// token operations, and userinfo. These routes live under
/// <c>/{instanceSlug}/...</c> on the public origin and authenticate with OAuth
/// client credentials or access tokens, not the management key.
/// </summary>
public sealed partial class TerraScaleAuthOidcClient(TerraScaleAuthClient client)
{
    /// <summary>Fetches the OIDC discovery document for the instance. Project issuers advertise only
    /// authorization_code and refresh_token grants and openid, email, profile and offline_access scopes;
    /// platform-only grants and device authorization metadata belong to the terrascale issuer.</summary>
    public Task<TerraScaleAuthOidcDiscovery> GetDiscoveryDocumentAsync(CancellationToken cancellationToken = default) =>
        client.GetMetadataAsync(
            InstanceRoute("/.well-known/openid-configuration"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthOidcDiscovery,
            cancellationToken);

    /// <summary>Fetches the instance's ES256 signing keys (JWKS).</summary>
    public Task<TerraScaleAuthJwkSet> GetJwksAsync(CancellationToken cancellationToken = default) =>
        client.GetMetadataAsync(
            InstanceRoute("/.well-known/jwks.json"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthJwkSet,
            cancellationToken);

    /// <summary>
    /// Exchanges an authorization code (PKCE) for tokens. The
    /// <paramref name="redirectUri"/> must match the authorize request exactly;
    /// supply the same <paramref name="codeVerifier"/> used to derive the challenge.
    /// </summary>
    public Task<TerraScaleAuthTokenResponse> ExchangeAuthorizationCodeAsync(
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

    /// <summary>
    /// Requests a machine-to-machine access token for the client itself
    /// (client credentials grant). API keys issue organization/project-scoped tokens;
    /// the audience is the producer's configured resource audience, never the regional host.
    /// </summary>
    public Task<TerraScaleAuthTokenResponse> RequestClientCredentialsTokenAsync(
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

    /// <summary>
    /// Exchanges a refresh token for new tokens. The service rotates refresh
    /// tokens; replaying a rotated token revokes the whole token family.
    /// </summary>
    public Task<TerraScaleAuthTokenResponse> RefreshTokenAsync(
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

    /// <summary>
    /// Revokes a token and its sign-in (RFC 7009), scoped to the calling platform or project client. Unknown or
    /// already-invalid tokens also succeed. Offline access tokens live until expiry.
    /// </summary>
    public Task RevokeTokenAsync(string clientId, string clientSecret, string token, CancellationToken cancellationToken = default)
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

    /// <summary>Reads the userinfo claims for one issued access token.</summary>
    public Task<TerraScaleAuthUserInfo> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default) =>
        client.GetWithBearerAsync(
            InstanceRoute("/oauth/userinfo"),
            accessToken,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUserInfo,
            cancellationToken);

    /// <summary>RFC 8693 exchange of this client's live sign-in token for organization/project authority.
    /// The producer rechecks the user's role. Scope is space-delimited; null requests the allowed scopes.
    /// Database restriction is unsupported by this producer's exchange grant.</summary>
    public Task<TerraScaleAuthTokenResponse> ExchangeTokenAsync(
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

/// <summary>The OIDC discovery document for one instance.</summary>
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

/// <summary>The instance's public signing keys.</summary>
public sealed record TerraScaleAuthJwkSet(
    [property: JsonPropertyName("keys")] IReadOnlyList<TerraScaleAuthJwk> Keys);

/// <summary>One ES256 public signing key (raw-coordinate form).</summary>
public sealed record TerraScaleAuthJwk(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("crv")] string Curve,
    [property: JsonPropertyName("alg")] string? Algorithm,
    [property: JsonPropertyName("use")] string? Use,
    [property: JsonPropertyName("kid")] string KeyId,
    [property: JsonPropertyName("x")] string X,
    [property: JsonPropertyName("y")] string Y);

/// <summary>A token endpoint response. <c>refresh_token</c> is absent for the
/// client credentials grant; <c>id_token</c> appears only for authorization-code
/// exchanges that requested the <c>openid</c> scope.</summary>
public sealed record TerraScaleAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    [property: JsonPropertyName("id_token")] string? IdToken = null,
    [property: JsonPropertyName("issued_token_type")] string? IssuedTokenType = null)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthTokenResponse).Name} {{ TokenType = {TokenType}, ExpiresIn = {ExpiresIn}, Scope = {Scope}, AccessToken = [REDACTED], RefreshToken = [REDACTED], IdToken = [REDACTED] }}";
}

/// <summary>Userinfo claims for one access token.</summary>
public sealed record TerraScaleAuthUserInfo(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("email_verified")] bool? EmailVerified,
    [property: JsonPropertyName("name")] string? Name);
