using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>Registers the inter-node gRPC listener and the <c>Replication</c> service.</summary>
public static class ReplicationExtensions
{
    /// <summary>Largest gRPC message in either direction: one 4 MiB application batch plus Protobuf framing.</summary>
    public const int MaxMessageBytes = (4 * 1024 * 1024) + (64 * 1024);

    /// <summary>
    /// Adds the peer listener (HTTP/2 over mutual TLS) and gRPC with <c>lz4</c> as the
    /// response encoding. No-op when the peer listener is disabled.
    /// </summary>
    public static WebApplicationBuilder AddTerraBaseReplication(this WebApplicationBuilder builder, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        if (options.PeerPort is not { } port)
        {
            return builder;
        }

        var clientCa = options.PeerClientCa!;
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = options.PeerCertificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => ChainsTo(certificate, clientCa);
            });
            listen.UseTerraBaseListener(TerraBaseListener.Peer);
        }));

        builder.Services.AddGrpc(grpc =>
        {
            grpc.CompressionProviders.Add(new Lz4CompressionProvider(MaxMessageBytes));
            grpc.ResponseCompressionAlgorithm = Lz4CompressionProvider.Name;
            grpc.MaxReceiveMessageSize = MaxMessageBytes;
            grpc.MaxSendMessageSize = MaxMessageBytes;
        });
        return builder;
    }

    /// <summary>Maps the <c>Replication</c> service onto the peer listener.</summary>
    public static WebApplication MapTerraBaseReplication(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<TerraBaseOptions>().PeerPort is not null)
        {
            app.MapGrpcService<ReplicationService>().WithMetadata(ListenerMetadata.Peer);
        }

        return app;
    }

    private static bool ChainsTo(X509Certificate2 certificate, X509Certificate2 authority)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        // ponytail: no revocation checks; add CRL/OCSP when peer certificates come from a real PKI.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2")); // TLS client authentication
        return chain.Build(certificate);
    }
}
