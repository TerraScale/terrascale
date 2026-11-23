using System.Net;
using TerraScale.Contracts.Consistency;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class PlatformHeaders(IDashboardIdentity identity, CausalBookmarks bookmarks) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        if (request.Headers.Authorization is null)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            request.Headers.Remove(TerraBaseHeaders.PeerCredentialHeader);
            request.Headers.TryAddWithoutValidation(TerraBaseHeaders.PeerCredentialHeader, credentials.TerraBasePeerSecret);
        }

        if (!request.Headers.Contains(ConsistencyHeaders.Bookmark) && bookmarks.For(request.RequestUri) is { } bookmark)
        {
            request.Headers.TryAddWithoutValidation(ConsistencyHeaders.Bookmark, bookmark);
        }

        var mutation = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head;
        var response = await ServerTiming.RpcAsync(string.Concat("platform ", request.Method.Method), mutation, () => base.SendAsync(request, cancellationToken));
        if (response.Headers.TryGetValues(ConsistencyHeaders.Bookmark, out var observed))
        {
            bookmarks.Observe(request.RequestUri, observed.FirstOrDefault());
        }

        return response;
    }
}
