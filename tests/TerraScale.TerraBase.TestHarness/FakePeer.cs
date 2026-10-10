using System.Net;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Testing;

/// <summary>
/// A scripted <c>Replication</c> receiver or <c>CatalogReplication</c> primary on its own mutual TLS listener, for tests that
/// need a peer to answer in ways a real node never does. Each stream runs <c>serve</c>.
/// </summary>
public sealed class FakePeer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;

    private FakePeer(WebApplication app, X509Certificate2 certificate, Uri address)
    {
        _app = app;
        _certificate = certificate;
        Address = address;
    }

    /// <summary>The <c>https://localhost</c> address to put in a node's <c>peer-addresses</c>.</summary>
    public Uri Address { get; }

    /// <summary>Starts a fake peer that serves the node certificate of <paramref name="tls"/>.</summary>
    public static Task<FakePeer> StartAsync(
        PeerTls tls, Func<IAsyncStreamReader<SyncRequest>, IServerStreamWriter<SyncResponse>, ServerCallContext, Task> serve,
        Action<HttpsConnectionAdapterOptions>? configureTls = null) =>
        StartAsync(tls, configureTls, services => services.AddSingleton(new Service(serve)), app => app.MapGrpcService<Service>());

    /// <summary>
    /// Starts a fake catalog primary (<c>CatalogReplication</c>) that serves the node certificate of <paramref name="tls"/>; each
    /// Follow stream runs <paramref name="serve"/>, and each <c>CatalogForwarding.Forward</c> call <paramref name="forward"/>.
    /// </summary>
    public static Task<FakePeer> StartCatalogAsync(
        PeerTls tls, Func<FollowRequest, IServerStreamWriter<FollowResponse>, ServerCallContext, Task> serve,
        Func<ForwardRequest, IServerStreamWriter<ForwardResponse>, ServerCallContext, Task>? forward = null) =>
        StartAsync(tls, null, services =>
        {
            services.AddSingleton(new CatalogService(serve));
            services.AddSingleton(new ForwardingService(forward ?? ((_, _, _) => throw new RpcException(new Status(StatusCode.Unimplemented, "no forwarding")))));
        }, app =>
        {
            app.MapGrpcService<CatalogService>();
            app.MapGrpcService<ForwardingService>();
        });

    private static async Task<FakePeer> StartAsync(
        PeerTls tls, Action<HttpsConnectionAdapterOptions>? configureTls, Action<IServiceCollection> register, Action<WebApplication> map)
    {
        ArgumentNullException.ThrowIfNull(tls);
        // Kestrel does not own a ServerCertificate: this peer disposes it after the application stops.
        var certificate = tls.LoadNodeCertificate();
        WebApplication? app = null;
        try
        {
            app = await BuildAsync(certificate, configureTls, register, map);
            var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            return new FakePeer(app, certificate, new Uri($"https://localhost:{port}"));
        }
        catch
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }

            certificate.Dispose();
            throw;
        }
    }

    private static async Task<WebApplication> BuildAsync(
        X509Certificate2 certificate, Action<HttpsConnectionAdapterOptions>? configureTls, Action<IServiceCollection> register, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = certificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
                configureTls?.Invoke(https);
            });
        }));
        builder.Services.AddGrpc();
        register(builder.Services);
        var app = builder.Build();
        map(app);
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A <c>Welcome</c> a real node would send for <paramref name="hello"/>, with <paramref name="limits"/> (by
    /// default the sender's own) and no applied prefix, and the receiving <paramref name="member"/> it names.
    /// </summary>
    public static SyncResponse Welcome(Hello hello, out PeerIdentity member, TransferLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var id = Google.Protobuf.ByteString.CopyFrom(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        member = hello.LocalIdentity.Clone();
        member.MemberId = member.MemberIncarnation = id;
        return new SyncResponse
        {
            Welcome = new Welcome
            {
                LocalIdentity = member,
                ProtocolVersion = new ProtocolVersion { Major = ReplicationProtocol.Major },
                EventFormatVersion = ReplicationProtocol.FormatVersion,
                RecordFormatVersion = ReplicationProtocol.FormatVersion,
                MergePolicyVersion = ReplicationProtocol.FormatVersion,
                Codecs = { PayloadCodec.Lz4BlockV1 },
                Limits = limits ?? hello.Limits,
            },
        };
    }

    /// <summary>The <c>DurableAck</c> a real node would send after applying <paramref name="batch"/> up to <paramref name="prefix"/>.</summary>
    public static SyncResponse Ack(PeerIdentity member, CommittedGroupBatch batch, ulong prefix)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new SyncResponse
        {
            Durable = new DurableAck
            {
                Context = new AckContext { AcknowledgingPeer = member, EventId = batch.EventId },
                DurablePrefix = prefix,
            },
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _certificate.Dispose();
    }

    private sealed class CatalogService(Func<FollowRequest, IServerStreamWriter<FollowResponse>, ServerCallContext, Task> serve)
        : CatalogReplication.CatalogReplicationBase
    {
        public override Task Follow(FollowRequest request, IServerStreamWriter<FollowResponse> responseStream, ServerCallContext context) =>
            serve(request, responseStream, context);
    }

    private sealed class ForwardingService(Func<ForwardRequest, IServerStreamWriter<ForwardResponse>, ServerCallContext, Task> forward)
        : CatalogForwarding.CatalogForwardingBase
    {
        public override Task Forward(ForwardRequest request, IServerStreamWriter<ForwardResponse> responseStream, ServerCallContext context) =>
            forward(request, responseStream, context);
    }

    private sealed class Service(Func<IAsyncStreamReader<SyncRequest>, IServerStreamWriter<SyncResponse>, ServerCallContext, Task> serve)
        : Features.Replication.V1.Replication.ReplicationBase
    {
        public override Task Sync(IAsyncStreamReader<SyncRequest> requestStream, IServerStreamWriter<SyncResponse> responseStream, ServerCallContext context) =>
            serve(requestStream, responseStream, context);
    }
}
