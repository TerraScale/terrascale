using Grpc.Net.Client;
using TerraBase.Engine.Cluster;

namespace TerraBase.Features.Replication;

/// <summary>gRPC channels to another member's internal listener: HTTP/2 without TLS over Fly's private network.</summary>
internal static class PeerChannel
{
    /// <summary>A channel to <paramref name="address"/> (<c>http://host:port</c>).</summary>
    public static GrpcChannel Create(Uri address)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ReplicationProtocol.ProgressTimeout,
            // HTTP/2 pings find a silently dropped connection in seconds, not at the TCP retransmission timeout.
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        };
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxSendMessageSize = ReplicationProtocol.MaxMessageBytes,
            MaxReceiveMessageSize = ReplicationProtocol.MaxMessageBytes,
        });
    }

    /// <summary>The internal listener of the catalog primary this member follows, or <see langword="null"/> while the membership names none.</summary>
    public static async ValueTask<Uri?> CatalogPrimaryAsync(this IMembership membership, CancellationToken cancellationToken) =>
        (await membership.RefreshAsync(cancellationToken).ConfigureAwait(false)).CatalogPrimary is { PeerAddress.Length: > 0 } primary
            ? new Uri(primary.PeerAddress)
            : null;
}
