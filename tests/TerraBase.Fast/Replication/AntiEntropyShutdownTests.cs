using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class AntiEntropyShutdownTests
{
    [Test]
    public async Task A_shutdown_during_a_peer_call_stops_anti_entropy_as_canceled()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider();
        var members = new FakeMemberStore();
        await using var peer = await HangingPeer.StartAsync();
        var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IMemberDirectory>(members);
        });
        var antiEntropy = server.Services.GetRequiredService<AntiEntropy>();
        try
        {
            // Listed after start: the fresh catalog opens alone, so only anti-entropy rounds call the peer.
            members.Join(new ClusterMember("zzzz-peer", peer.Address, "test-region", ""));
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                clock.Advance(AntiEntropy.Round);
                return Task.FromResult(peer.Entered.Task.IsCompleted);
            });

            // The shutdown cancels the in-flight digest call: a normal exit, not a background-service failure.
            await server.DisposeAsync().AsTask().WaitAsync(token);
            await Assert.That(antiEntropy.ExecuteTask!.IsCanceled).IsTrue();
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // A peer whose digests never answer until the caller cancels.
    private sealed class HangingPeer : Features.Replication.V1.Replication.ReplicationBase, IAsyncDisposable
    {
        private WebApplication? _app;

        public string Address { get; private set; } = "";

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<HangingPeer> StartAsync()
        {
            var peer = new HangingPeer();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(peer);
            peer._app = builder.Build();
            peer._app.MapGrpcService<HangingPeer>();
            await peer._app.StartAsync();
            peer.Address = peer._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return peer;
        }

        public override async Task<DigestReply> Digest(DigestRequest request, ServerCallContext context)
        {
            _ = Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            throw new RpcException(new Status(StatusCode.Unavailable, "unreachable"));
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
