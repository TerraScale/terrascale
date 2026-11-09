using System.Net;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal sealed record ReplicaPeer(string Region, string Name, Uri Internal);

internal interface IReplicaPeers
{
    ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken);
}

internal sealed class PeersFileReplicaPeers(TerraBaseOptions options, RegionalOwners owners) : IReplicaPeers
{
    public async ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await owners.NodesAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Name != options.MachineId)
            .Select(node => new ReplicaPeer(node.Region, node.Name, node.InternalListener(options.InternalPort)))];
    }
}

internal static class ServingNodeExtensions
{
    extension(ServingNode node)
    {
        public Uri InternalListener(int port) => new UriBuilder(Uri.UriSchemeHttp, node.Address.ToString(), port).Uri;
    }
}

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
