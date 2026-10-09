using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>Registers the inter-node gRPC listener, the <c>Replication</c> service and the sender.</summary>
public static class ReplicationExtensions
{
    /// <summary>
    /// Adds the peer listener (HTTP/2 over mutual TLS with certificates read again when their files change)
    /// and the <c>Replication</c> and <c>CatalogReplication</c> services, plus the sender when <c>peer-addresses</c> is
    /// set and the catalog follower and <see cref="ICatalogPrimaryClient"/> when <c>catalog-primary</c> is. Registers <see cref="ICatalogReadiness"/> and
    /// <see cref="IReplicationWait"/>; nothing else when the peer listener is disabled. Payloads are LZ4 application envelopes; gRPC transport compression is not used.
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
            builder.Services.AddSingleton<CatalogForwarder>();
            builder.Services.AddSingleton<ICatalogPrimaryClient, CatalogPrimaryClient>();
        }

        if (options.PeerAddresses.Count == 0)
        {
            builder.Services.AddSingleton<IReplicationWait, NoReplicationWait>();
            builder.Services.AddSingleton<IReplicationStatus, NoReplicationStatus>();
            builder.Services.AddSingleton<IReplicationPressure, NoReplicationWait>();
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
        builder.Services.AddSingleton<IAntiEntropyState, AntiEntropyState>();
        builder.Services.AddSingleton<AntiEntropyService>();
        builder.Services.AddHostedService<AntiEntropyWorker>();
        builder.Services.AddSingleton<CatalogFeed>();
        builder.Services.AddSingleton<ForwardedRequests>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = ReplicationProtocol.MaxMessageBytes;
            grpc.MaxSendMessageSize = ReplicationProtocol.MaxMessageBytes;
        });
        if (options.PeerAddresses.Count > 0)
        {
            builder.Services.AddSingleton<ReplicationSender>();
            builder.Services.AddSingleton<IReplicationWait>(provider => provider.GetRequiredService<ReplicationSender>());
            builder.Services.AddSingleton<IReplicationStatus>(provider => provider.GetRequiredService<ReplicationSender>());
            builder.Services.AddSingleton<IReplicationPressure>(provider => provider.GetRequiredService<ReplicationSender>());
            builder.Services.AddHostedService(provider => provider.GetRequiredService<ReplicationSender>());
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
            app.MapGrpcService<AntiEntropyService>().WithMetadata(ListenerMetadata.Peer);
            app.MapGrpcService<CatalogFeed>().WithMetadata(ListenerMetadata.Peer);
            app.MapGrpcService<ForwardedRequests>().WithMetadata(ListenerMetadata.Peer);
        }

        return app;
    }

    /// <summary>Keeps anti-entropy response backpressure inside its admission and transmission deadline.</summary>
    public static WebApplication UseTerraBaseAntiEntropyResponses(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<TerraBaseOptions>().PeerPort is not null)
        {
            app.Use(async (context, next) =>
            {
                await next(context).ConfigureAwait(false);
                await AntiEntropyService.FlushResponseAsync(context).ConfigureAwait(false);
            });
        }

        return app;
    }

    /// <summary>
    /// Takes the application pipeline that serves requests a catalog follower forwards; register it first, so a forwarded
    /// request passes every middleware a request accepted by a listener does.
    /// </summary>
    public static WebApplication UseTerraBaseForwardedRequests(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<ForwardedRequests>() is { } forwarded)
        {
            app.Use(forwarded.Capture);
        }

        return app;
    }

    /// <summary>
    /// Register after authorization: a catalog follower forwards an authenticated client-listener request to an endpoint the
    /// catalog primary serves (<see cref="ServedByCatalogPrimary"/>) to its primary, and the primary serves a forwarded request
    /// only on such an endpoint (404 otherwise). The administrative listener's requests are never forwarded.
    /// </summary>
    public static WebApplication UseTerraBaseCatalogForwarding(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var forwarder = app.Services.GetService<CatalogForwarder>();
        app.Use((context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (context.Features.Get<ForwardedRequests.ForwardedRequest>() is not null)
            {
                if (ServedByCatalogPrimary.Serves(endpoint))
                {
                    return next(context);
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            if (forwarder is null || Listeners.ArrivedOn(context) != TerraBaseListener.Client)
            {
                return next(context);
            }

            return ServedByCatalogPrimary.Always(endpoint) ? forwarder.ForwardAsync(context)
                : ServedByCatalogPrimary.Serves(endpoint) ? forwarder.ForwardWhenWritingAsync(context, next)
                : next(context);
        });
        return app;
    }
}
