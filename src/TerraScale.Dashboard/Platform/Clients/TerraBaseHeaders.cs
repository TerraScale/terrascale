using System.Net;
using TerraScale.Contracts.Consistency;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class TerraBaseHeaders(IDashboardIdentity identity, CausalBookmarks bookmarks, bool state) : DelegatingHandler
{
    public const string PeerCredentialHeader = "X-TerraBase-Peer-Credential";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = request.RequestUri?.Scheme == Uri.UriSchemeHttps ? HttpVersionPolicy.RequestVersionOrHigher : HttpVersionPolicy.RequestVersionExact;
        request.Headers.Remove(PeerCredentialHeader);
        request.Headers.TryAddWithoutValidation(PeerCredentialHeader, credentials.TerraBasePeerSecret);
        if (credentials.Caller is { } caller && request.Headers.Authorization is null)
        {
            request.Headers.Remove(CallerCredential.HeaderName);
            request.Headers.TryAddWithoutValidation(CallerCredential.HeaderName, caller.Token);
        }

        if (!request.Headers.Contains(ConsistencyHeaders.Bookmark) && bookmarks.For(request.RequestUri) is { } bookmark)
        {
            request.Headers.TryAddWithoutValidation(ConsistencyHeaders.Bookmark, bookmark);
        }

        var mutation = !state && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head;
        var response = await ServerTiming.RpcAsync(Operation(request), mutation, () => base.SendAsync(request, cancellationToken));
        if (response.Headers.TryGetValues(ConsistencyHeaders.Bookmark, out var observed))
        {
            bookmarks.Observe(request.RequestUri, observed.FirstOrDefault());
        }

        return response;
    }

    private static string Operation(HttpRequestMessage request) =>
        string.Concat(request.Method.Method, " ", request.RequestUri?.AbsolutePath is { } path && path.LastIndexOf('/') is var slash and >= 0 ? path[slash..] : "/");
}

public sealed class StrongConsistency : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(ConsistencyHeaders.Consistency);
        request.Headers.TryAddWithoutValidation(ConsistencyHeaders.Consistency, ConsistencyHeaders.StrongValue);
        return base.SendAsync(request, cancellationToken);
    }
}
