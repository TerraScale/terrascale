using System.Net;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

/// <summary>A node of another region, reached on its internal listener.</summary>
/// <param name="Region">Its region.</param>
/// <param name="Name">Its node name.</param>
/// <param name="Internal">Its internal listener, e.g. <c>http://10.0.0.2:8081/</c>.</param>
internal sealed record ReplicaPeer(string Region, string Name, Uri Internal);

/// <summary>The nodes of the other regions, one per region: a database's home node is its home region's node.</summary>
internal interface IReplicaPeers
{
    ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The ts-orch peers file (<c>TS_PEERS_FILE</c>): every node listens on the same internal port on its private address.
/// A region of several nodes is reached at its first node in the file.
/// </summary>
internal sealed class PeersFileReplicaPeers(TerraBaseOptions options) : IReplicaPeers
{
    public async ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Region != options.Region).DistinctBy(node => node.Region)
            .Select(node => new ReplicaPeer(node.Region, node.Name, new UriBuilder(Uri.UriSchemeHttp, node.Address.ToString(), options.InternalPort).Uri))];
    }
}

/// <summary>Requests to other nodes' internal listeners: HTTP/2 without TLS on the private mesh, with the peer credential.</summary>
internal static class PeerHttp
{
    public static HttpClient Create(InternalCredential credential) =>
        new(new PeerRequests(credential)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(10),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            },
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

    private sealed class PeerRequests(InternalCredential credential) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            var secret = credential.CorrelationKey
                ?? throw new InvalidOperationException($"{InternalCredential.EnvironmentVariable} is required for internal requests");
            request.Headers.Remove(InternalCredential.HeaderName);
            request.Headers.Add(InternalCredential.HeaderName, secret);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
