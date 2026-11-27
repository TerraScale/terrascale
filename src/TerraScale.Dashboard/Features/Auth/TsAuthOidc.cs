using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed partial class TsAuthGateway
    {
        public async Task<string> AuthorizationUrlAsync(OidcFlow flow, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            var metadata = await MetadataAsync(Client(), cancellationToken);
            var endpoint = TryTrustedEndpoint(metadata.AuthorizationEndpoint) ?? throw new HttpRequestException("ts-auth published an invalid OIDC endpoint.");
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

        public async Task<ExchangeResult> ExchangeAsync(string code, OidcFlow flow, DateTimeOffset? credentialExpiresAt, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            {
                return new ExchangeRejected();
            }

            var credentials = await identity.ReadAsync(cancellationToken);
            var tokenOutcome = await Client().Oidc.TryExchangeAuthorizationCodeAsync(
                credentials.AuthClientId, credentials.AuthClientSecret, code, flow.RedirectUri, flow.Verifier, credentialExpiresAt, cancellationToken);
            return tokenOutcome switch
            {
                TsAuthTokenResponse tokens => WithCredentialExpiry(await TrySessionAsync(tokens, credentials, flow.Nonce, cancellationToken), credentialExpiresAt),
                OAuthTokenInvalidGrant => new ExchangeRejected(),
                OAuthTokenInvalidRequest when credentialExpiresAt is not null =>
                    new DashboardValidation("This token could not be created with that expiry. Choose a shorter expiry."),
                OAuthTokenInvalidRequest invalid => new ExchangeUnavailable("TsAuthHttpError", invalid.Message),
                OAuthTokenNotStarted { StatusCode: HttpStatusCode.TooManyRequests, RetryAfter: var retryAfter } => new ExchangeRateLimited(retryAfter),
                OAuthTokenNotStarted notStarted => new ExchangeUnavailable(notStarted.ErrorType, notStarted.Message),
                OAuthTokenUnavailable unavailable => new ExchangeUnavailable(unavailable.ErrorType, unavailable.Message),
            };
        }

        private static ExchangeResult WithCredentialExpiry(ExchangeResult outcome, DateTimeOffset? credentialExpiresAt) => outcome switch
        {
            SessionRecord session => session.Actor is not null && credentialExpiresAt is not null
                ? new DashboardValidation("An impersonated session can't create long-lived tokens.")
                : session,
            DashboardValidation validation => validation,
            ExchangeRejected rejected => rejected,
            ExchangeRateLimited limited => limited,
            ExchangeUnavailable unavailable => unavailable,
        };

        public async Task<SessionRefreshOutcome> TryRefreshAsync(SessionRecord session, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (session.RefreshToken is not { } refreshToken || string.IsNullOrWhiteSpace(refreshToken))
            {
                return new SessionRefreshRejected();
            }

            var deadlineAt = time.GetUtcNow() + timeout;
            var credentialsOutcome = await identity.TryReadAsync(timeout, cancellationToken);
            return credentialsOutcome switch
            {
                DashboardCredentials credentials => await RefreshWithCredentialsAsync(session, refreshToken, credentials, deadlineAt, cancellationToken),
                DashboardCredentialsUnavailable unavailable => new SessionRefreshNotStarted(unavailable.ErrorType, null, "credentials_unavailable", unavailable.Message),
            };
        }

        private async Task<SessionRefreshOutcome> RefreshWithCredentialsAsync(
            SessionRecord session,
            string refreshToken,
            DashboardCredentials credentials,
            DateTimeOffset deadlineAt,
            CancellationToken cancellationToken)
        {
            var client = Client();
            var refresh = new RefreshContext(
                session,
                credentials,
                client,
                deadlineAt,
                TryMetadataAsync(client, Remaining(deadlineAt), cancellationToken),
                TrySigningKeysAsync(client, Remaining(deadlineAt), cancellationToken));
            var tokenOutcome = await client.Oidc.TryRefreshTokenAsync(
                credentials.AuthClientId, credentials.AuthClientSecret, refreshToken, Remaining(deadlineAt), cancellationToken);
            return tokenOutcome switch
            {
                TsAuthTokenResponse tokens => await RefreshWithTokensAsync(refresh, tokens, cancellationToken),
                OAuthTokenInvalidGrant => new SessionRefreshRejected(),
                OAuthTokenInvalidRequest invalid => new SessionRefreshNotStarted("TsAuthHttpError", invalid.StatusCode, "invalid_request", invalid.Message),
                OAuthTokenNotStarted notStarted => new SessionRefreshNotStarted(notStarted.ErrorType, notStarted.StatusCode, notStarted.ErrorCode, notStarted.Message),
                OAuthTokenUnavailable unavailable => new SessionRefreshFailed(unavailable.ErrorType, unavailable.StatusCode, unavailable.ErrorCode, unavailable.Message),
            };
        }

        private async Task<SessionRefreshOutcome> RefreshWithTokensAsync(RefreshContext refresh, TsAuthTokenResponse tokens, CancellationToken cancellationToken)
        {
            if (tokens.ExpiresIn is <= 0 or > 86400
                || !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                return new SessionRefreshRejected();
            }

            var userRead = TrySessionUserInfoAsync(refresh.Client, refresh.Session with { AccessToken = tokens.AccessToken }, Remaining(refresh.DeadlineAt), cancellationToken);
            var metadataOutcome = await refresh.MetadataRead;
            return metadataOutcome switch
            {
                TsAuthOidcDiscovery metadata => await RefreshWithMetadataAsync(refresh, tokens, metadata, userRead, cancellationToken),
                OidcDiscoveryUnavailable unavailable => new SessionRefreshFailed(unavailable.ErrorType, unavailable.StatusCode, unavailable.ErrorCode, unavailable.Message),
            };
        }

        private async Task<SessionRefreshOutcome> RefreshWithMetadataAsync(
            RefreshContext refresh,
            TsAuthTokenResponse tokens,
            TsAuthOidcDiscovery metadata,
            Task<UserInfoOutcome> userRead,
            CancellationToken cancellationToken)
        {
            if (TryTrustedIssuer(metadata.Issuer) is not { } issuer)
            {
                return new SessionRefreshRejected();
            }

            if (tokens.IdToken is not { Length: > 0 } idToken)
            {
                return await RefreshWithUserAsync(refresh, tokens, issuer, userRead, cancellationToken);
            }

            var idKeysOutcome = await TrySigningKeysAsync(refresh.Client, idToken, refresh.KeysRead, refresh.DeadlineAt, cancellationToken);
            return idKeysOutcome switch
            {
                TsAuthJwkSet keys => AuthIdToken.TryValidate(idToken, metadata.Issuer, refresh.Credentials.AuthClientId, null, keys, time.GetUtcNow()) is { } claims
                    && claims.Subject == refresh.Session.UserId
                        ? await RefreshWithUserAsync(refresh, tokens, issuer, userRead, cancellationToken)
                        : new SessionRefreshRejected(),
                OidcJwksUnavailable unavailable => new SessionRefreshFailed(unavailable.ErrorType, unavailable.StatusCode, unavailable.ErrorCode, unavailable.Message),
            };
        }

        private async Task<SessionRefreshOutcome> RefreshWithUserAsync(
            RefreshContext refresh,
            TsAuthTokenResponse tokens,
            string issuer,
            Task<UserInfoOutcome> userRead,
            CancellationToken cancellationToken)
        {
            var session = refresh.Session;
            var userOutcome = await userRead;
            return userOutcome switch
            {
                TsAuthUserInfo user => user.Subject != session.UserId || user.EmailVerified != true || user.Email?.Trim().ToLowerInvariant() != session.Email ||
                    !Acceptable(user) || user.Actor?.Subject != session.Actor
                        ? new SessionRefreshRejected()
                        : await RefreshWithAccessKeysAsync(refresh, tokens, issuer, cancellationToken),
                UserInfoUnauthorized => new SessionRefreshRejected(),
                UserInfoUnavailable unavailable => new SessionRefreshFailed(unavailable.ErrorType, unavailable.StatusCode, unavailable.ErrorCode, unavailable.Message),
            };
        }

        private async Task<SessionRefreshOutcome> RefreshWithAccessKeysAsync(RefreshContext refresh, TsAuthTokenResponse tokens, string issuer, CancellationToken cancellationToken)
        {
            var session = refresh.Session;
            var accessKeysOutcome = await TrySigningKeysAsync(refresh.Client, tokens.AccessToken, refresh.KeysRead, refresh.DeadlineAt, cancellationToken);
            return accessKeysOutcome switch
            {
                TsAuthJwkSet keys => TryAccessTokenExpiry(tokens.AccessToken, issuer, keys) is { } expiresAt
                    ? session with
                    {
                        AccessToken = tokens.AccessToken,
                        RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? session.RefreshToken : tokens.RefreshToken,
                        IdToken = tokens.IdToken is { Length: > 0 } ? tokens.IdToken : session.IdToken,
                        ExpiresAt = expiresAt,
                        RefreshLease = "",
                        RefreshLeaseUntil = null
                    }
                    : new SessionRefreshRejected(),
                OidcJwksUnavailable unavailable => new SessionRefreshFailed(unavailable.ErrorType, unavailable.StatusCode, unavailable.ErrorCode, unavailable.Message),
            };
        }

        public async Task<string> EndSessionUrlAsync(string dashboardOrigin, string? idToken, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(dashboardOrigin, UriKind.Absolute, out var dashboard) || dashboard.UserInfo.Length != 0 ||
                dashboard.AbsolutePath != "/" || dashboard.Query.Length != 0 || dashboard.Fragment.Length != 0 ||
                dashboard.Scheme is not ("https" or "http"))
            {
                throw new ArgumentException("Invalid dashboard origin.", nameof(dashboardOrigin));
            }

            var credentials = await identity.ReadAsync(cancellationToken);
            var metadataOutcome = await TryMetadataAsync(Client(), timeout: null, cancellationToken);
            var metadata = metadataOutcome switch
            {
                TsAuthOidcDiscovery value => value,
                OidcDiscoveryUnavailable => null,
            };
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.EndSessionEndpoint) || TryTrustedEndpoint(metadata.EndSessionEndpoint) is not { } endpoint)
            {
                return "/login";
            }

            return endpoint + (idToken is { Length: > 0 } ? "?id_token_hint=" + Uri.EscapeDataString(idToken) + "&" : "?") +
                "post_logout_redirect_uri=" + Uri.EscapeDataString(new Uri(dashboard, "/login").AbsoluteUri) +
                "&client_id=" + Uri.EscapeDataString(credentials.AuthClientId);
        }

        private async Task<ExchangeResult> TrySessionAsync(TsAuthTokenResponse tokens, DashboardCredentials credentials, string? nonce, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresIn is <= 0 or > 86400 ||
                !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return new ExchangeRejected();
            }

            var client = Client();
            var metadataOutcome = await TryMetadataAsync(client, timeout: null, cancellationToken);
            return metadataOutcome switch
            {
                TsAuthOidcDiscovery metadata => await SessionWithMetadataAsync(client, tokens, credentials, nonce, metadata, cancellationToken),
                OidcDiscoveryUnavailable unavailable => new ExchangeUnavailable(unavailable.ErrorType, unavailable.Message),
            };
        }

        private async Task<ExchangeResult> SessionWithMetadataAsync(
            TsAuthClient client,
            TsAuthTokenResponse tokens,
            DashboardCredentials credentials,
            string? nonce,
            TsAuthOidcDiscovery metadata,
            CancellationToken cancellationToken)
        {
            if (TryTrustedIssuer(metadata.Issuer) is not { } issuer || !metadata.IdTokenSigningAlgValuesSupported.Contains("ES256", StringComparer.Ordinal))
            {
                return new ExchangeRejected();
            }

            var keysOutcome = await TrySigningKeysAsync(client, tokens.IdToken, initialRead: null, deadlineAt: null, cancellationToken);
            return keysOutcome switch
            {
                TsAuthJwkSet keys => AuthIdToken.TryValidate(tokens.IdToken, metadata.Issuer, credentials.AuthClientId, nonce, keys, time.GetUtcNow()) is { } claims
                    && TryAccessTokenExpiry(tokens.AccessToken, issuer, keys) is { } expiresAt
                        ? await SessionWithClaimsAsync(
                            client,
                            claims,
                            new SessionRecord(claims.Subject, claims.Name, claims.Email, tokens.AccessToken, tokens.RefreshToken, expiresAt)
                            { IdToken = tokens.IdToken, OrganizationPicker = claims.OrganizationPicker },
                            cancellationToken)
                        : new ExchangeRejected(),
                OidcJwksUnavailable unavailable => new ExchangeUnavailable(unavailable.ErrorType, unavailable.Message),
            };
        }

        private async Task<ExchangeResult> SessionWithClaimsAsync(TsAuthClient client, AuthIdentity claims, SessionRecord session, CancellationToken cancellationToken)
        {
            var userOutcome = await TrySessionUserInfoAsync(client, session, timeout: null, cancellationToken);
            return userOutcome switch
            {
                TsAuthUserInfo user => user.Subject != claims.Subject || user.EmailVerified != true || user.Email?.Trim().ToLowerInvariant() != claims.Email || !Acceptable(user)
                    ? new ExchangeRejected()
                    : await ActiveSessionAsync(user.Actor is { } actor ? session with { Actor = actor.Subject, RefreshToken = null } : session, cancellationToken),
                UserInfoUnauthorized => new ExchangeRejected(),
                UserInfoUnavailable unavailable => new ExchangeUnavailable(unavailable.ErrorType, unavailable.Message),
            };
        }

        private async Task<ExchangeResult> ActiveSessionAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            var activity = await IsActiveAsync(session, cancellationToken);
            return activity switch
            {
                SessionStillActive => session,
                SessionEnded => new ExchangeRejected(),
                SessionActivityUnavailable unavailable => new ExchangeUnavailable(unavailable.ErrorType, unavailable.Message),
            };
        }

        private TimeSpan Remaining(DateTimeOffset deadlineAt) => deadlineAt - time.GetUtcNow();

        private static bool Acceptable(TsAuthUserInfo user) =>
            user.IsAnonymous != true && user.Actor is null or { Subject.Length: > 0 and <= 128 } && user.Actor?.Subject != user.Subject;

        private DateTimeOffset AccessTokenExpiry(string accessToken, string issuer, TsAuthJwkSet keys) =>
            TryAccessTokenExpiry(accessToken, issuer, keys) ?? throw new SignInRejectedException();

        private DateTimeOffset? TryAccessTokenExpiry(string accessToken, string issuer, TsAuthJwkSet keys)
        {
            var now = time.GetUtcNow();
            if (AuthJwtClaims.TryRead(accessToken, issuer, issuer, keys, now, "at+jwt") is not { } claims
                || !claims.TryGetProperty("exp", out var exp)
                || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var expiry)
                || expiry > now.AddDays(1).ToUnixTimeSeconds())
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(expiry);
        }

        private static string PlatformIssuer => new Uri(TsAuthOrigin.Value, "/" + Tenant).AbsoluteUri;

        private static string TrustedIssuer(string issuer) => TryTrustedIssuer(issuer) ?? throw new SignInRejectedException();

        private static string? TryTrustedIssuer(string issuer) =>
            Uri.TryCreate(issuer, UriKind.Absolute, out var uri) && SameOrigin(uri) &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsoluteUri == PlatformIssuer
                ? uri.AbsoluteUri
                : null;

        private static bool SameOrigin(Uri uri) =>
            uri.Scheme == TsAuthOrigin.Value.Scheme && uri.Port == TsAuthOrigin.Value.Port
            && string.Equals(uri.Host, TsAuthOrigin.Value.Host, StringComparison.OrdinalIgnoreCase);

        private static string? TryTrustedEndpoint(string endpoint) =>
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && SameOrigin(uri) &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            (uri.AbsolutePath == "/" + Tenant || uri.AbsolutePath.StartsWith("/" + Tenant + "/", StringComparison.Ordinal))
                ? uri.AbsoluteUri.TrimEnd('/')
                : null;

        private static async Task<TsAuthUserInfo> UserInfoAsync(TsAuthClient client, string accessToken, CancellationToken cancellationToken)
        {
            var outcome = await client.Oidc.TryGetUserInfoAsync(accessToken, timeout: null, cancellationToken);
            return outcome switch
            {
                TsAuthUserInfo user => user,
                UserInfoUnauthorized => throw new SignInRejectedException(),
                UserInfoUnavailable failure => throw ToDependencyException(failure.ErrorType, failure.StatusCode, failure.ErrorCode, failure.Message),
            };
        }

        private static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private sealed record RefreshContext(
            SessionRecord Session,
            DashboardCredentials Credentials,
            TsAuthClient Client,
            DateTimeOffset DeadlineAt,
            Task<OidcDiscoveryOutcome> MetadataRead,
            Task<OidcKeySetOutcome> KeysRead);
    }

    public sealed record ExchangeRejected;

    public sealed record ExchangeRateLimited(TimeSpan? RetryAfter);

    public sealed record ExchangeUnavailable(string ErrorType, string Message);

    public readonly union ExchangeResult(SessionRecord, DashboardValidation, ExchangeRejected, ExchangeRateLimited, ExchangeUnavailable);
}
