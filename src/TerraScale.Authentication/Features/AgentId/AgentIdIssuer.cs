using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Oidc;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.AgentId;

public static class AgentIdIssuer
{
    public const string Slug = "agents";

    public const string AuthorizePath = "/" + Slug + "/oauth/authorize";

    public const string TokenPath = "/" + Slug + "/oauth/token";

    public const string UserInfoPath = "/" + Slug + "/oauth/userinfo";

    public const string ProofRoute = "/" + Slug + "/v1/authorize/{transaction}/proof";

    public const string ActorTypeClaim = "actor_type";

    public const string OwnerSubjectClaim = "owner_sub";

    public const string AgentClaim = "ts_agentid_agent";

    public const string OwnerClaim = "ts_agentid_owner";

    public const string ProofTransactionClaim = "txn";

    public const string ProofClientClaim = "client_id";

    public static readonly IReadOnlyList<string> Routes = ["oauth/authorize", "oauth/token", "oauth/userinfo", OidcIssuer.ConfigurationPath, OidcIssuer.JwksPath];

    public static readonly IReadOnlyList<string> SupportedScopes = [Scopes.OpenId, Scopes.Profile];

    public static readonly IReadOnlyList<string> SupportedClaims =
        [Claims.Subject, Claims.Name, ActorTypeClaim, OwnerSubjectClaim, Claims.Nonce, Claims.Issuer, Claims.Audience, Claims.IssuedAt, Claims.ExpiresAt, Claims.JwtId];

    public static Uri Issuer(AuthOptions options) => OidcIssuer.Issuer(options, Slug);

    public static string ProofUrl(AuthOptions options, string transaction) =>
        new Uri(options.PublicOrigin, ProofRoute.Replace("{transaction}", transaction, StringComparison.Ordinal)).AbsoluteUri;

    public static bool Guarded(PathString path) =>
        (path.Value ?? string.Empty).TrimEnd('/') is var trimmed
        && (trimmed.Equals(TokenPath, StringComparison.OrdinalIgnoreCase) || trimmed.Equals(AuthorizePath, StringComparison.OrdinalIgnoreCase)
            || trimmed.Split('/') is [_, var slug, "v1", "authorize", _, "proof"] && slug.Equals(Slug, StringComparison.OrdinalIgnoreCase));

    public static bool IsOpenClient(string? clientId) =>
        clientId is { Length: > 0 and <= 255 } && Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri))
        && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0
        && string.Equals(uri.GetLeftPart(UriPartial.Authority), clientId, StringComparison.Ordinal);

    public static bool AllowsRedirect(string clientId, string? redirectUri) =>
        IsOpenClient(clientId) && Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect)
        && redirect.Fragment.Length == 0 && redirect.UserInfo.Length == 0
        && string.Equals(redirect.GetLeftPart(UriPartial.Authority), clientId, StringComparison.Ordinal);

    public static OidcApplication? OpenClient(string clientId) => IsOpenClient(clientId)
        ? new OidcApplication
        {
            Id = clientId,
            ClientId = clientId,
            InstanceId = Slug,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = clientId,
            Permissions =
            [
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.Profile,
            ],
            Version = string.Create(CultureInfo.InvariantCulture, $"agentid-open:{clientId}"),
        }
        : null;

    public static string Pairwise(AuthOptions options, string kind, string clientId, string id)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = OidcIssuer.Derive(options, "ts-auth agentid pairwise subject v1");
        return Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{kind}\n{clientId}\n{id}")));
    }

    public static ClaimsPrincipal Principal(AuthOptions options, string clientId, AgentRecord agent, string ownerId, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(agent);
        List<string> granted = [.. scopes.Intersect(SupportedScopes, StringComparer.Ordinal)];
        var profile = granted.Contains(Scopes.Profile);
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
            .SetClaim(Claims.Subject, Pairwise(options, "agent", clientId, agent.Id))
            .SetClaim(ActorTypeClaim, "agent")
            .SetClaim(Claims.Name, profile ? agent.Name : null)
            .SetClaim(OwnerSubjectClaim, profile ? Pairwise(options, "owner", clientId, ownerId) : null)
            .SetClaim(AgentClaim, agent.Id)
            .SetClaim(OwnerClaim, ownerId)
            .SetScopes(granted);
        identity.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject or ActorTypeClaim or Claims.Name or OwnerSubjectClaim => [Destinations.AccessToken, Destinations.IdentityToken],
            AgentClaim or OwnerClaim => [Destinations.AccessToken],
            _ => [],
        });
        return new ClaimsPrincipal(identity);
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback && (uri.Host == "localhost" || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
