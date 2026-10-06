using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>Registers the inter-node gRPC listener, the <c>Replication</c> service and the sender.</summary>
public static class ReplicationExtensions
{
    /// <summary>
    /// Adds the peer listener (HTTP/2 over mutual TLS with certificates read again when their files change)
    /// and the <c>Replication</c> and <c>CatalogReplication</c> services, plus the sender when <c>peer-addresses</c> is
    /// set and the catalog follower when <c>catalog-primary</c> is. Registers <see cref="ICatalogReadiness"/>; nothing else
    /// when the peer listener is disabled. Payloads are LZ4 application envelopes; gRPC transport compression is not used.
    /// </summary>
    public static WebApplicationBuilder AddTerraBaseReplication(this WebApplicationBuilder builder, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        if (options.CatalogPrimary is null)
        {
            builder.Services.AddSingleton<ICatalogReadiness, CatalogPrimaryReadiness>();
        }
        else
        {
            builder.Services.AddSingleton<CatalogFollower>();
            builder.Services.AddSingleton<ICatalogReadiness>(provider => provider.GetRequiredService<CatalogFollower>());
            builder.Services.AddHostedService(provider => provider.GetRequiredService<CatalogFollower>());
        }

        if (options.PeerPort is not { } port)
        {
            return builder;
        }

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port, listen =>
        {
            var credentials = kestrel.ApplicationServices.GetRequiredService<PeerCredentials>();
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                OnConnection = context =>
                {
                    var snapshot = credentials.Capture();
                    context.Connection.ConnectionClosed.Register(static state => ((PeerCredentials.Lease)state!).Dispose(), snapshot);
                    return ValueTask.FromResult(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = snapshot.CertificateContext,
                        CertificateChainPolicy = snapshot.Policy(PeerTlsFiles.ClientAuthentication),
                        ClientCertificateRequired = true,
                        ApplicationProtocols = [SslApplicationProtocol.Http2],
                        RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                            certificate is X509Certificate2 client && snapshot.IsTrusted(client, PeerTlsFiles.ClientAuthentication, chain),
                    });
                },
            });
            listen.UseTerraBaseListener(TerraBaseListener.Peer);
        }));

        builder.Services.AddSingleton<PeerCredentials>();
        builder.Services.AddSingleton<ReplicationMetrics>();
        builder.Services.AddSingleton<IReplicationJournal, EngineReplicationJournal>();
        builder.Services.AddSingleton<ReplicationService>();
        builder.Services.AddSingleton<CatalogFeed>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = ReplicationProtocol.MaxMessageBytes;
            grpc.MaxSendMessageSize = ReplicationProtocol.MaxMessageBytes;
        });
        if (options.PeerAddresses.Count > 0)
        {
            builder.Services.AddHostedService<ReplicationSender>();
        }

        return builder;
    }

    /// <summary>Maps the <c>Replication</c> and <c>CatalogReplication</c> services onto the peer listener.</summary>
    public static WebApplication MapTerraBaseReplication(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<TerraBaseOptions>().PeerPort is not null)
        {
            app.MapGrpcService<ReplicationService>().WithMetadata(ListenerMetadata.Peer);
            app.MapGrpcService<CatalogFeed>().WithMetadata(ListenerMetadata.Peer);
        }

        return app;
    }
}
