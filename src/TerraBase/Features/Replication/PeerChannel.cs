using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using TerraBase.Configuration;

namespace TerraBase.Features.Replication;

/// <summary>gRPC channels to another member's peer listener over mutual TLS.</summary>
internal static class PeerChannel
{
    /// <summary>
    /// A channel to <paramref name="address"/> that presents <paramref name="snapshot"/>'s certificate chain and trusts only
    /// its authorities, for server authentication with the address's host name.
    /// </summary>
    public static GrpcChannel Create(Uri address, PeerCredentials.Lease snapshot)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ReplicationProtocol.ProgressTimeout,
            // HTTP/2 pings find a silently dropped connection in seconds, not at the TCP retransmission timeout.
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = snapshot.Policy(PeerTlsFiles.ServerAuthentication),
                ClientCertificateContext = snapshot.CertificateContext,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && certificate is X509Certificate2 server && snapshot.IsTrusted(server, PeerTlsFiles.ServerAuthentication, chain),
            },
        };
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxSendMessageSize = ReplicationProtocol.MaxMessageBytes,
            MaxReceiveMessageSize = ReplicationProtocol.MaxMessageBytes,
        });
    }
}
