using Grpc.Net.Client;

namespace TerraBase.Features.Replication;

/// <summary>gRPC channels to another member's internal listener: HTTP/2 without TLS over Fly's private network.</summary>
internal static class PeerChannel
{
    /// <summary>A channel to <paramref name="address"/> (<c>http://host:port</c>).</summary>
    public static GrpcChannel Create(Uri address, PeerCredential credential)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = ReplicationProtocol.ProgressTimeout,
            // HTTP/2 pings find a silently dropped connection in seconds, not at the TCP retransmission timeout.
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        };
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = credential.Wrap(handler),
            DisposeHttpClient = true,
            MaxSendMessageSize = ReplicationProtocol.MaxMessageBytes,
            MaxReceiveMessageSize = ReplicationProtocol.MaxMessageBytes,
        });
    }
}
