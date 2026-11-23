using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public interface IProjectAccessTokens
    {
        Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway : IProjectAccessTokens
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<(string Token, DateTimeOffset Until)>>> exchanged = new(StringComparer.Ordinal);
        private const string TerraBaseAudience = "https://api.terrascale.tech";

        public static readonly IReadOnlySet<string> ProjectScopes = new HashSet<string>(StringComparer.Ordinal)
        {
            "databases:read", "databases:write", "sql:read", "sql:write"
        };

        public async Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            bool Usable(Lazy<Task<(string Token, DateTimeOffset Until)>> entry)
            {
                return !entry.Value.IsCompleted || entry.Value.IsCompletedSuccessfully && entry.Value.Result.Until > now;
            }

            if (exchanged.Count > 10_000)
            {
                foreach (var stale in exchanged.Where(entry => !Usable(entry.Value)).Select(entry => entry.Key).ToArray())
                {
                    exchanged.TryRemove(stale, out _);
                }
            }

            var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                string.Join('\n', session.AccessToken, organizationId, projectId, string.Join(' ', scopes.Order(StringComparer.Ordinal))))));
            Lazy<Task<(string, DateTimeOffset)>> Fresh()
            {
                return new(() => ExchangeAsync(session, organizationId, projectId, scopes, CancellationToken.None));
            }

            var entry = exchanged.AddOrUpdate(key, _ => Fresh(), (_, current) => Usable(current) ? current : Fresh());
            try { return (await entry.Value.WaitAsync(cancellationToken)).Token; }
            catch when (entry.Value.IsFaulted || entry.Value.IsCanceled)
            {
                exchanged.TryRemove(new(key, entry));
                throw;
            }
        }

        private async Task<(string Token, DateTimeOffset Until)> ExchangeAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId) ||
                session.ExpiresAt <= time.GetUtcNow() || string.IsNullOrWhiteSpace(session.AccessToken) ||
                scopes is not { Count: > 0 } || scopes.Any(scope => !ProjectScopes.Contains(scope)) || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count)
            {
                throw new HttpRequestException("Project authorization is unavailable.");
            }

            var scope = string.Join(' ', scopes.Order(StringComparer.Ordinal));
            var credentials = await identity.ReadAsync(cancellationToken);
            var client = Client();
            var discoveryRead = MetadataAsync(client, cancellationToken);
            var keysRead = SigningKeysAsync(client, cancellationToken);
            _ = ObservePublicReadsAsync(discoveryRead, keysRead);
            TsAuthTokenResponse response;
            try
            {
                response = await client.Oidc.ExchangeTokenAsync(credentials.AuthClientId, credentials.AuthClientSecret, session.AccessToken,
                    organizationId, projectId, scope, cancellationToken);
            }
            catch (TsAuthException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests)
            {
                throw (error.StatusCode, error.ErrorCode) switch
                {
                    (HttpStatusCode.BadRequest, "invalid_target") => new HttpRequestException("The project is unavailable.", null, HttpStatusCode.NotFound),
                    (HttpStatusCode.BadRequest, "invalid_scope") => new HttpRequestException("The role does not grant the requested scopes.", null, HttpStatusCode.Forbidden),
                    (HttpStatusCode.BadRequest, "invalid_grant") => new HttpRequestException("The sign-in is no longer valid.", null, HttpStatusCode.Unauthorized),
                    (HttpStatusCode.TooManyRequests, _) => new HttpRequestException("ts-auth is rate limiting token exchange.", null, HttpStatusCode.TooManyRequests),
                    _ => new HttpRequestException("Project authorization is unavailable.", null, error.StatusCode)
                };
            }
            try
            {
                var discovery = await discoveryRead;
                var issuer = TrustedIssuer(discovery.Issuer);
                if (response.TokenType != "Bearer" || response.IssuedTokenType != "urn:ietf:params:oauth:token-type:access_token" ||
                    response.ExpiresIn is <= 0 or > 900 ||
                    response.Scope is not null && !SameScopes(response.Scope, scope) || response.RefreshToken is not null || response.IdToken is not null)
                {
                    throw new SignInRejectedException();
                }

                var now = time.GetUtcNow();
                var claims = AuthJwtClaims.Read(response.AccessToken, issuer, TerraBaseAudience,
                    await keysRead, now, "at+jwt");
                if (claims.GetProperty("sub").GetString() != session.UserId ||
                    claims.GetProperty("client_id").GetString() != credentials.AuthClientId ||
                    claims.GetProperty("org_id").GetString() != organizationId || claims.GetProperty("project_id").GetString() != projectId ||
                    !SameScopes(claims.GetProperty("scope").GetString(), scope) || claims.TryGetProperty("database", out _) ||
                    claims.GetProperty("exp").GetInt64() > now.AddMinutes(15).ToUnixTimeSeconds() ||
                    claims.GetProperty("exp").GetInt64() > session.ExpiresAt.ToUnixTimeSeconds())
                {
                    throw new SignInRejectedException();
                }

                return (response.AccessToken, DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64()).AddSeconds(-30));
            }
            catch (Exception error) when (error is SignInRejectedException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { throw new HttpRequestException("The issuer returned invalid project authorization."); }
        }

        private static bool SameScopes(string? actual, string expected)
        {
            return actual is not null &&
            actual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).SequenceEqual(expected.Split(' '), StringComparer.Ordinal);
        }
    }
}
