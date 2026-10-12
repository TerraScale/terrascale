using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public interface IProjectAccessTokens
{
    Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, Uri resourceOrigin, CancellationToken cancellationToken);
}

// The served ts-auth RFC 8693 exchange supplies the caller's project authority.
// The pinned official SDK has no exchange method; discovery and JWKS use it.
// Each request rechecks membership at ts-auth, without caching permissions.
public sealed partial class TsAuthGateway : IProjectAccessTokens
{
    public async Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, Uri resourceOrigin, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId) ||
            session.ExpiresAt <= time.GetUtcNow() || string.IsNullOrWhiteSpace(session.AccessToken) ||
            !resourceOrigin.IsAbsoluteUri || resourceOrigin.Scheme != "https" || !resourceOrigin.IsDefaultPort ||
            resourceOrigin.UserInfo.Length != 0 || resourceOrigin.AbsolutePath != "/" || resourceOrigin.Query.Length != 0 || resourceOrigin.Fragment.Length != 0 ||
            !resourceOrigin.Host.EndsWith(".api.terrascale.tech", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Project authorization is unavailable.");
        var credentials = await identity.ReadAsync(cancellationToken);
        if (credentials.AuthInstanceSlug != "terrascale") throw new HttpRequestException("Project authorization is unavailable.");
        var origin = AuthOrigin;
        var client = Client(credentials, origin);
        var discovery = await client.Oidc.GetDiscoveryDocumentAsync(cancellationToken);
        try
        {
            var issuer = TrustedIssuer(discovery.Issuer, origin, credentials.AuthInstanceSlug);
            var endpoint = TrustedEndpoint(discovery.TokenEndpoint, origin, credentials.AuthInstanceSlug);
            if (new Uri(endpoint).AbsolutePath != "/terrascale/oauth/token") throw new SignInRejectedException();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            Authenticate(request, credentials);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["subject_token"] = session.AccessToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["organization"] = organizationId,
                ["project"] = projectId,
                ["scope"] = "databases:read"
            });
            using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Project authorization is unavailable.", null, response.StatusCode);
            using var body = await PlatformJson.ReadAsync(response.Content, cancellationToken);
            var result = body.RootElement;
            var token = result.GetProperty("access_token").GetString();
            if (result.GetProperty("token_type").GetString() != "Bearer" ||
                result.GetProperty("issued_token_type").GetString() != "urn:ietf:params:oauth:token-type:access_token" ||
                result.GetProperty("expires_in").GetInt32() is <= 0 or > 900 ||
                result.GetProperty("scope").GetString() != "databases:read" ||
                result.TryGetProperty("refresh_token", out _) || result.TryGetProperty("id_token", out _))
                throw new SignInRejectedException();
            var now = time.GetUtcNow();
            var claims = AuthJwtClaims.Read(token, issuer, resourceOrigin.GetLeftPart(UriPartial.Authority),
                await client.Oidc.GetJwksAsync(cancellationToken), now, "at+jwt");
            if (claims.GetProperty("sub").GetString() != session.UserId ||
                claims.GetProperty("client_id").GetString() != credentials.AuthClientId ||
                claims.GetProperty("org_id").GetString() != organizationId || claims.GetProperty("project_id").GetString() != projectId ||
                claims.GetProperty("scope").GetString() != "databases:read" || claims.TryGetProperty("database", out _) ||
                claims.GetProperty("exp").GetInt64() > now.AddMinutes(15).ToUnixTimeSeconds() ||
                claims.GetProperty("exp").GetInt64() > session.ExpiresAt.ToUnixTimeSeconds())
                throw new SignInRejectedException();
            return token!;
        }
        catch (Exception error) when (error is SignInRejectedException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new HttpRequestException("The issuer returned invalid project authorization."); }
    }
}
