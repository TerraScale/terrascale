using System.Net;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public interface IProjectAccessTokens
{
    // Requests exactly the action's scopes for the signed-in user. The user-project exchange cannot restrict a
    // database, so a token carrying a database claim is refused. Every failure is HttpRequestException; its
    // StatusCode tells ts-auth's refusals apart: 404 invalid_target (a project the user does not reach), 403
    // invalid_scope (the role lacks a scope), 401 invalid_grant (sign in again), 429 rate limited. Any other
    // status, or none, means ts-auth or the network failed.
    Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken);
}

// ts-auth's RFC 8693 exchange (ts-dotnet ExchangeTokenAsync) supplies the caller's project
// authority. Each request rechecks membership at ts-auth, without caching permissions.
public sealed partial class TsAuthGateway : IProjectAccessTokens
{
    // ts-auth gives every organization and project token TerraBase's one audience, valid in every region.
    private const string TerraBaseAudience = "https://api.terrascale.tech";

    public static readonly IReadOnlySet<string> ProjectScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        "databases:read", "databases:write", "documents:read", "documents:write"
    };

    public async Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId) ||
            session.ExpiresAt <= time.GetUtcNow() || string.IsNullOrWhiteSpace(session.AccessToken) ||
            scopes is not { Count: > 0 } || scopes.Any(scope => !ProjectScopes.Contains(scope)) || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count)
            throw new HttpRequestException("Project authorization is unavailable.");
        var scope = string.Join(' ', scopes.Order(StringComparer.Ordinal));
        var credentials = await identity.ReadAsync(cancellationToken);
        var client = Client();
        var discovery = await client.Oidc.GetDiscoveryDocumentAsync(cancellationToken);
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
            var issuer = TrustedIssuer(discovery.Issuer);
            if (response.TokenType != "Bearer" || response.IssuedTokenType != "urn:ietf:params:oauth:token-type:access_token" ||
                response.ExpiresIn is <= 0 or > 900 ||
                // OpenIddict (ts-auth's 7.7.1) answers scope only when it differs from the request; the token's claim decides.
                response.Scope is not null && !SameScopes(response.Scope, scope) || response.RefreshToken is not null || response.IdToken is not null)
                throw new SignInRejectedException();
            var now = time.GetUtcNow();
            var claims = AuthJwtClaims.Read(response.AccessToken, issuer, TerraBaseAudience,
                await client.Oidc.GetJwksAsync(cancellationToken), now, "at+jwt");
            if (claims.GetProperty("sub").GetString() != session.UserId ||
                claims.GetProperty("client_id").GetString() != credentials.AuthClientId ||
                claims.GetProperty("org_id").GetString() != organizationId || claims.GetProperty("project_id").GetString() != projectId ||
                !SameScopes(claims.GetProperty("scope").GetString(), scope) || claims.TryGetProperty("database", out _) ||
                claims.GetProperty("exp").GetInt64() > now.AddMinutes(15).ToUnixTimeSeconds() ||
                claims.GetProperty("exp").GetInt64() > session.ExpiresAt.ToUnixTimeSeconds())
                throw new SignInRejectedException();
            return response.AccessToken;
        }
        catch (Exception error) when (error is SignInRejectedException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new HttpRequestException("The issuer returned invalid project authorization."); }
    }

    // A scope string is an unordered space-separated set (RFC 6749 section 3.3).
    private static bool SameScopes(string? actual, string expected) => actual is not null &&
        actual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).SequenceEqual(expected.Split(' '), StringComparer.Ordinal);
}
