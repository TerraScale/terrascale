using System.Net;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth
{
    public interface IProjectAccessTokens
    {
        // Requests exactly the action's scopes for the signed-in user. The user-project exchange cannot restrict a
        // database, so a token carrying a database claim is refused. Every failure is HttpRequestException; its
        // StatusCode tells ts-auth's refusals apart: 404 invalid_target (a project the user does not reach), 403
        // invalid_scope (the role lacks a scope), 401 invalid_grant (sign in again), 429 rate limited. Any other
        // status, or none, means ts-auth or the network failed.
        Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken);
    }

    // ts-auth's RFC 8693 exchange (ts-dotnet ExchangeTokenAsync) supplies the caller's project authority. An
    // exchanged token is reused until 30 s before it expires, bound to the exact session access token, project
    // and scopes; ts-auth caps it at that access token's own expiry, and a refreshed sign-in gets new ones.
    // Every page still rechecks membership at ts-auth when it loads its account context, so reuse only saves
    // the 0.5-1.5 s exchange that each TerraBase read used to repeat.
    public sealed partial class TsAuthGateway : IProjectAccessTokens
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<(string Token, DateTimeOffset Until)>>> exchanged = new(StringComparer.Ordinal);
        // ts-auth gives every organization and project token TerraBase's one audience, valid in every region.
        private const string TerraBaseAudience = "https://api.terrascale.tech";

        public static readonly IReadOnlySet<string> ProjectScopes = new HashSet<string>(StringComparer.Ordinal)
        {
            "databases:read", "databases:write", "documents:read", "documents:write"
        };

        public async Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            // A pending exchange is shared; a finished one serves until its reuse ends; a failed one is never reused.
            bool Usable(Lazy<Task<(string Token, DateTimeOffset Until)>> entry) =>
                !entry.Value.IsCompleted || entry.Value.IsCompletedSuccessfully && entry.Value.Result.Until > now;
            if (exchanged.Count > 10_000)
            {
                foreach (var stale in exchanged.Where(entry => !Usable(entry.Value)).Select(entry => entry.Key).ToArray())
                {
                    exchanged.TryRemove(stale, out _);
                }
            }

            var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                string.Join('\n', session.AccessToken, organizationId, projectId, string.Join(' ', scopes.Order(StringComparer.Ordinal))))));
            Lazy<Task<(string, DateTimeOffset)>> Fresh() => new(() => ExchangeAsync(session, organizationId, projectId, scopes, CancellationToken.None));
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
            // Public verification material and the exchange are independent. Cold scopes share
            // metadata within this request; all token claims are still checked before dispatch.
            var discoveryRead = MetadataAsync(client, cancellationToken);
            var keysRead = SigningKeysAsync(client, cancellationToken);
            _ = ObservePublicReadsAsync(discoveryRead, keysRead);
            TerraScaleAuthTokenResponse response;
            try
            {
                response = await client.Oidc.ExchangeTokenAsync(credentials.AuthClientId, credentials.AuthClientSecret, session.AccessToken,
                    organizationId, projectId, scope, cancellationToken);
            }
            // ts-auth refuses with RFC 6749 errors (PlatformGrants.cs); the status tells callers which one.
            catch (TerraScaleAuthException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests)
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
                    // OpenIddict (ts-auth's 7.7.1) answers scope only when it differs from the request; the token's claim decides.
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

        // A scope string is an unordered space-separated set (RFC 6749 section 3.3).
        private static bool SameScopes(string? actual, string expected)
        {
            return actual is not null &&
            actual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).SequenceEqual(expected.Split(' '), StringComparer.Ordinal);
        }
    }
}
