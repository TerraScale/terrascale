using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed partial class TsAuthGateway
    {
        // ts-auth answers prompt=login with login_required, so no prompt is sent; sign-out ends its session instead.
        public async Task<string> AuthorizationUrlAsync(OidcFlow flow, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            var metadata = await Client().Oidc.GetDiscoveryDocumentAsync(cancellationToken);
            var endpoint = TrustedEndpoint(metadata.AuthorizationEndpoint);
            var fields = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = credentials.AuthClientId,
                ["redirect_uri"] = flow.RedirectUri,
                ["scope"] = "openid email profile offline_access",
                ["state"] = flow.State,
                ["nonce"] = flow.Nonce,
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier))),
                ["code_challenge_method"] = "S256"
            };
            return endpoint + "?" + string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));
        }

        public async Task<SessionRecord> ExchangeAsync(string code, OidcFlow flow, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            {
                throw new SignInRejectedException();
            }

            var credentials = await identity.ReadAsync(cancellationToken);
            TerraScaleAuthTokenResponse tokens;
            try
            {
                tokens = await Client().Oidc.ExchangeAuthorizationCodeAsync(
                    credentials.AuthClientId, credentials.AuthClientSecret, code, flow.RedirectUri, flow.Verifier, cancellationToken);
            }
            catch (TerraScaleAuthException error) when (error.ErrorCode == "invalid_grant") { throw new SignInRejectedException(); }
            return await SessionAsync(tokens, credentials, flow.Nonce, cancellationToken);
        }

        public async Task<SessionRecord> RefreshAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                throw new SignInRejectedException();
            }

            var credentials = await identity.ReadAsync(cancellationToken);
            var client = Client();
            var metadataRead = MetadataAsync(client, cancellationToken);
            var keysRead = SigningKeysAsync(client, cancellationToken);
            // Observe speculative failures even when refresh fails or returns no ID token.
            _ = ObservePublicReadsAsync(metadataRead, keysRead);
            TerraScaleAuthTokenResponse tokens;
            try
            {
                tokens = await client.Oidc.RefreshTokenAsync(credentials.AuthClientId, credentials.AuthClientSecret, session.RefreshToken, cancellationToken);
            }
            catch (TerraScaleAuthException error) when (error.ErrorCode == "invalid_grant" || error.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
            { throw new SignInRejectedException(); }
            catch (TerraScaleAuthException error) when (error.ErrorCode == "temporarily_unavailable" || error.StatusCode is System.Net.HttpStatusCode.TooManyRequests)
            { throw new RefreshNotStartedException(error); }
            catch (HttpRequestException error) when (error is not TerraScaleAuthException && error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
            { throw new RefreshNotStartedException(error); }
            if (tokens.ExpiresIn is <= 0 or > 86400 || !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                throw new SignInRejectedException();
            }

            var userRead = SessionUserInfoAsync(client, session with { AccessToken = tokens.AccessToken }, cancellationToken);
            _ = ObservePublicReadsAsync(userRead);
            // OIDC allows refresh answers without an ID token. A new ID token must
            // still prove the original subject, and userinfo remains mandatory.
            if (tokens.IdToken is { Length: > 0 })
            {
                var metadata = await metadataRead;
                _ = TrustedIssuer(metadata.Issuer);
                var claims = AuthIdToken.Validate(tokens.IdToken, metadata.Issuer, credentials.AuthClientId, null,
                    await keysRead, time.GetUtcNow());
                if (claims.Subject != session.UserId)
                {
                    throw new SignInRejectedException();
                }
            }
            var user = await userRead;
            if (user.Subject != session.UserId || user.EmailVerified != true || user.Email?.Trim().ToLowerInvariant() != session.Email)
            {
                throw new SignInRejectedException();
            }

            var refreshed = session with
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? session.RefreshToken : tokens.RefreshToken,
                IdToken = tokens.IdToken is { Length: > 0 } ? tokens.IdToken : session.IdToken,
                ExpiresAt = time.GetUtcNow().AddSeconds(tokens.ExpiresIn),
                RefreshLease = "",
                RefreshLeaseUntil = null
            };
            return refreshed;
        }

        private static async Task ObservePublicReadsAsync(params Task[] reads)
        {
            await Task.WhenAll(reads).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // ts-auth ends its own session, shows its hosted "Signed out" page, then continues to the dashboard's /login.
        public async Task<string> EndSessionUrlAsync(string dashboardOrigin, string? idToken, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(dashboardOrigin, UriKind.Absolute, out var dashboard) || dashboard.UserInfo.Length != 0 ||
                dashboard.AbsolutePath != "/" || dashboard.Query.Length != 0 || dashboard.Fragment.Length != 0 ||
                dashboard.Scheme is not ("https" or "http"))
            {
                throw new ArgumentException("Invalid dashboard origin.", nameof(dashboardOrigin));
            }

            try
            {
                var credentials = await identity.ReadAsync(cancellationToken);
                var metadata = await Client().Oidc.GetDiscoveryDocumentAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(metadata.EndSessionEndpoint))
                {
                    return "/login";
                }

                var endpoint = TrustedEndpoint(metadata.EndSessionEndpoint);
                return endpoint + (idToken is { Length: > 0 } ? "?id_token_hint=" + Uri.EscapeDataString(idToken) + "&" : "?") +
                    "post_logout_redirect_uri=" + Uri.EscapeDataString(new Uri(dashboard, "/login").AbsoluteUri) +
                    "&client_id=" + Uri.EscapeDataString(credentials.AuthClientId);
            }
            catch (HttpRequestException) { return "/login"; }
        }

        private async Task<SessionRecord> SessionAsync(TerraScaleAuthTokenResponse tokens, DashboardCredentials credentials, string? nonce, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresIn is <= 0 or > 86400 ||
                !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                throw new SignInRejectedException();
            }

            var client = Client();
            var metadata = await client.Oidc.GetDiscoveryDocumentAsync(cancellationToken);
            _ = TrustedIssuer(metadata.Issuer);
            if (!metadata.IdTokenSigningAlgValuesSupported.Contains("ES256", StringComparer.Ordinal))
            {
                throw new SignInRejectedException();
            }

            var keys = await client.Oidc.GetJwksAsync(cancellationToken);
            var claims = AuthIdToken.Validate(tokens.IdToken, metadata.Issuer, credentials.AuthClientId, nonce, keys, time.GetUtcNow());
            var session = new SessionRecord(claims.Subject, claims.Name, claims.Email, tokens.AccessToken, tokens.RefreshToken, time.GetUtcNow().AddSeconds(tokens.ExpiresIn))
            { IdToken = tokens.IdToken };
            // The session's own userinfo read, so the activity check below reuses this answer instead of a second
            // sequential ts-auth round trip on every sign-in.
            var user = await SessionUserInfoAsync(client, session, cancellationToken);
            if (user.Subject != claims.Subject || user.EmailVerified != true || user.Email?.Trim().ToLowerInvariant() != claims.Email)
            {
                throw new SignInRejectedException();
            }

            if (!await IsActiveAsync(session, cancellationToken))
            {
                throw new SignInRejectedException();
            }

            return session;
        }

        private static string TrustedIssuer(string issuer)
        {
            var expected = new Uri(AuthOrigin, "/" + Tenant);
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.AbsoluteUri != expected.AbsoluteUri)
            {
                throw new SignInRejectedException();
            }

            return uri.AbsoluteUri;
        }

        private static string TrustedEndpoint(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.Host != AuthOrigin.Host ||
                (uri.AbsolutePath != "/" + Tenant && !uri.AbsolutePath.StartsWith("/" + Tenant + "/", StringComparison.Ordinal)))
            {
                throw new HttpRequestException("ts-auth published an invalid OIDC endpoint.");
            }

            return uri.AbsoluteUri.TrimEnd('/');
        }

        private static async Task<TerraScaleAuthUserInfo> UserInfoAsync(TerraScaleAuthClient client, string accessToken, CancellationToken cancellationToken)
        {
            try { return await client.Oidc.GetUserInfoAsync(accessToken, cancellationToken); }
            catch (TerraScaleAuthException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            { throw new SignInRejectedException(); }
        }

        private static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
