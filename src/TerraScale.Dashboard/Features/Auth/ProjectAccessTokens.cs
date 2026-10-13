using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
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

// The served ts-auth RFC 8693 exchange supplies the caller's project authority.
// The pinned official SDK has no exchange method; discovery and JWKS use it.
// Each request rechecks membership at ts-auth, without caching permissions.
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
        try
        {
            var issuer = TrustedIssuer(discovery.Issuer);
            // Credentials go to the fixed token endpoint, never to a URL taken from discovery.
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
            Authenticate(request, credentials);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["subject_token"] = session.AccessToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["organization"] = organizationId,
                ["project"] = projectId,
                ["scope"] = scope
            });
            using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw await ExchangeRefusalAsync(response, cancellationToken);
            using var body = await PlatformJson.ReadAsync(response.Content, cancellationToken);
            var result = body.RootElement;
            var token = result.GetProperty("access_token").GetString();
            if (result.GetProperty("token_type").GetString() != "Bearer" ||
                result.GetProperty("issued_token_type").GetString() != "urn:ietf:params:oauth:token-type:access_token" ||
                result.GetProperty("expires_in").GetInt32() is <= 0 or > 900 ||
                // OpenIddict (ts-auth's 7.7.1) answers scope only when it differs from the request; the token's claim decides.
                result.TryGetProperty("scope", out var granted) && !SameScopes(granted.GetString(), scope) ||
                result.TryGetProperty("refresh_token", out _) || result.TryGetProperty("id_token", out _))
                throw new SignInRejectedException();
            var now = time.GetUtcNow();
            var claims = AuthJwtClaims.Read(token, issuer, TerraBaseAudience,
                await client.Oidc.GetJwksAsync(cancellationToken), now, "at+jwt");
            if (claims.GetProperty("sub").GetString() != session.UserId ||
                claims.GetProperty("client_id").GetString() != credentials.AuthClientId ||
                claims.GetProperty("org_id").GetString() != organizationId || claims.GetProperty("project_id").GetString() != projectId ||
                !SameScopes(claims.GetProperty("scope").GetString(), scope) || claims.TryGetProperty("database", out _) ||
                claims.GetProperty("exp").GetInt64() > now.AddMinutes(15).ToUnixTimeSeconds() ||
                claims.GetProperty("exp").GetInt64() > session.ExpiresAt.ToUnixTimeSeconds())
                throw new SignInRejectedException();
            return token!;
        }
        catch (Exception error) when (error is SignInRejectedException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new HttpRequestException("The issuer returned invalid project authorization."); }
    }

    // ts-auth answers token-exchange refusals with RFC 6749 errors (PlatformGrants.cs: invalid_target, invalid_scope).
    private static async Task<HttpRequestException> ExchangeRefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = "";
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest)
            try
            {
                using var body = await PlatformJson.ReadAsync(response.Content, cancellationToken);
                if (body.RootElement.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String) error = code.GetString() ?? "";
            }
            catch (JsonException) { }
        return (response.StatusCode, error) switch
        {
            (System.Net.HttpStatusCode.BadRequest, "invalid_target") => new HttpRequestException("The project is unavailable.", null, System.Net.HttpStatusCode.NotFound),
            (System.Net.HttpStatusCode.BadRequest, "invalid_scope") => new HttpRequestException("The role does not grant the requested scopes.", null, System.Net.HttpStatusCode.Forbidden),
            (System.Net.HttpStatusCode.BadRequest, "invalid_grant") => new HttpRequestException("The sign-in is no longer valid.", null, System.Net.HttpStatusCode.Unauthorized),
            (System.Net.HttpStatusCode.TooManyRequests, _) => new HttpRequestException("ts-auth is rate limiting token exchange.", null, System.Net.HttpStatusCode.TooManyRequests),
            _ => new HttpRequestException("Project authorization is unavailable.", null, response.StatusCode)
        };
    }

    // A scope string is an unordered space-separated set (RFC 6749 section 3.3).
    private static bool SameScopes(string? actual, string expected) => actual is not null &&
        actual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).SequenceEqual(expected.Split(' '), StringComparer.Ordinal);
}
