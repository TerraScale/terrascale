using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Testing;

/// <summary>
/// A scripted <c>CatalogReplication</c> primary on its own HTTP/2 listener, for tests that
/// need a peer to answer in ways a real node never does. Each stream runs <c>serve</c>.
/// </summary>
public sealed class FakePeer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakePeer(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>The <c>http://127.0.0.1</c> address to put in a node's <c>peer-addresses</c>.</summary>
    public Uri Address { get; }

    /// <summary>
    /// Starts a fake catalog primary (<c>CatalogReplication</c>); each
    /// Follow stream runs <paramref name="serve"/>, and each <c>CatalogForwarding.Forward</c> call <paramref name="forward"/>.
    /// </summary>
    public static Task<FakePeer> StartCatalogAsync(
        PeerTls tls, Func<FollowRequest, IServerStreamWriter<FollowResponse>, ServerCallContext, Task> serve,
        Func<ForwardRequest, IServerStreamWriter<ForwardResponse>, ServerCallContext, Task>? forward = null) =>
        StartAsync(tls, services =>
        {
            services.AddSingleton(new CatalogService(serve));
            services.AddSingleton(new ForwardingService(forward ?? ((_, _, _) => throw new RpcException(new Status(StatusCode.Unimplemented, "no forwarding")))));
        }, app =>
        {
            app.MapGrpcService<CatalogService>();
            app.MapGrpcService<ForwardingService>();
        });

    private static async Task<FakePeer> StartAsync(PeerTls tls, Action<IServiceCollection> register, Action<WebApplication> map)
    {
        ArgumentNullException.ThrowIfNull(tls);
        var app = await BuildAsync(register, map);
        var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        return new FakePeer(app, new Uri($"http://127.0.0.1:{port}"));
    }

    private static async Task<WebApplication> BuildAsync(Action<IServiceCollection> register, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
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
}
