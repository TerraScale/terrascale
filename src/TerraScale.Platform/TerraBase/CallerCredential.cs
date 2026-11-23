using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Platform.TerraBase;

public sealed record CallerCredential(string? Authorization, bool Peer)
{
    public static CallerCredential? From(HttpRequest request, string peerSecret)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(peerSecret);
        if (request.Headers.TryGetValue(PlatformOptions.PeerCredentialHeader, out var peer) && peer.Count == 1 && peer[0] is { Length: 64 } presented
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(peerSecret)))
        {
            return new CallerCredential(null, true);
        }

        return request.Headers.Authorization is { Count: 1 } authorization && authorization[0] is { Length: > 0 and <= 16 * 1024 } value
            ? new CallerCredential(value, false)
            : null;
    }

    public static CallerCredential FromToken(string token) => new("Bearer " + token, false);

    public string? BearerToken =>
        Authorization is { } value && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value["Bearer ".Length..].Trim() : null;

    public bool IsJwt => BearerToken is { } token && token.Count(static character => character == '.') == 2;

    public string CacheKey(AuthorizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var material = string.Join('\n', Peer ? "peer" : Authorization, request.Project, request.Database, string.Join(' ', request.Scopes));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public void Apply(HttpRequestMessage request, string peerSecret)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation(PlatformOptions.PeerCredentialHeader, peerSecret);
        if (!Peer && Authorization is { } authorization)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
    }

    public void Forward(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Peer)
        {
            request.Headers.TryAddWithoutValidation(PlatformHeaders.CallerPeer, "1");
        }
        else if (Authorization is { } authorization)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
    }
}
