using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthOidcClient(TsAuthClient client)
    {
        public Task<TsAuthOidcDiscovery> GetDiscoveryDocumentAsync(CancellationToken cancellationToken = default)
        {
            return client.GetMetadataAsync(
            InstanceRoute("/.well-known/openid-configuration"),
                TsAuthJsonContext.Default.TsAuthOidcDiscovery,
            cancellationToken);
        }

        public Task<TsAuthJwkSet> GetJwksAsync(CancellationToken cancellationToken = default)
        {
            return client.GetMetadataAsync(
            InstanceRoute("/.well-known/jwks.json"),
                TsAuthJsonContext.Default.TsAuthJwkSet,
            cancellationToken);
        }

        public Task<TsAuthTokenResponse> ExchangeAuthorizationCodeAsync(
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
                TsAuthIntegrationJsonContext.Default.TsAuthTokenResponse,
                cancellationToken);
        }

        public Task<TsAuthTokenResponse> RefreshTokenAsync(
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
                TsAuthIntegrationJsonContext.Default.TsAuthTokenResponse,
                cancellationToken);
        }

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

        public Task<TsAuthUserInfo> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            return client.GetWithBearerAsync(
            InstanceRoute("/oauth/userinfo"),
            accessToken,
                TsAuthJsonContext.Default.TsAuthUserInfo,
            cancellationToken);
        }

        public Task<TsAuthTokenResponse> ExchangeTokenAsync(
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
            return client.SendOAuthFormAsync(TokenRoute(), form, TsAuthCredentials.Basic(clientId, clientSecret),
                TsAuthIntegrationJsonContext.Default.TsAuthTokenResponse, cancellationToken);
        }

        private string InstanceRoute(string relative)
        {
            return "/terrascale" + relative;
        }

        private string TokenRoute()
        {
            return InstanceRoute("/oauth/token");
        }
    }

    public sealed record TsAuthOidcDiscovery(
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

    public sealed record TsAuthJwkSet(
        [property: JsonPropertyName("keys")] IReadOnlyList<TsAuthJwk> Keys);

    public sealed record TsAuthJwk(
        [property: JsonPropertyName("kty")] string KeyType,
        [property: JsonPropertyName("crv")] string Curve,
        [property: JsonPropertyName("alg")] string? Algorithm,
        [property: JsonPropertyName("use")] string? Use,
        [property: JsonPropertyName("kid")] string KeyId,
        [property: JsonPropertyName("x")] string X,
        [property: JsonPropertyName("y")] string Y);

    public sealed record TsAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope = null,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
        [property: JsonPropertyName("id_token")] string? IdToken = null,
        [property: JsonPropertyName("issued_token_type")] string? IssuedTokenType = null)
    {
        public override string ToString()
        {
            return $"{typeof(TsAuthTokenResponse).Name} {{ TokenType = {TokenType}, ExpiresIn = {ExpiresIn}, Scope = {Scope}, AccessToken = [REDACTED], RefreshToken = [REDACTED], IdToken = [REDACTED] }}";
        }
    }

    public sealed record TsAuthUserInfo(
        [property: JsonPropertyName("sub")] string Subject,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] bool? EmailVerified,
        [property: JsonPropertyName("name")] string? Name);
}
