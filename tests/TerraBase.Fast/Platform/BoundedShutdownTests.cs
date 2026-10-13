using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Bogus;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Features.Backup;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class BoundedShutdownTests
{
    [Test]
    public async Task A_peer_that_never_sends_hello_cannot_consume_the_hosted_service_budget()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var entered = new HandshakeProbe();
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            services.AddSingleton(entered);
            services.AddGrpc(options => options.Interceptors.Add<HandshakeProbe>());
        });
        using var channel = PeerChannel.Create(server.BaseAddress);
        using var peer = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: token);
        await entered.Entered.Task.WaitAsync(token);
        var completion = server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
        var elapsed = Stopwatch.StartNew();
        server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await completion.WaitAsync(TimeSpan.FromSeconds(45), token);
        TestContext.Current!.OutputWriter.WriteLine($"shutdown_seconds={elapsed.Elapsed.TotalSeconds:F3} peer_without_hello=true");
        await Assert.That(elapsed.Elapsed).IsLessThan(Shutdown.HostedBudget);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Active_changes_and_peer_streams_stop_before_storage_closes_within_budget(bool stalledBackup)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        await using var api = await DocumentApi.StartAsync(bucket.Configure, services =>
        {
            bucket.Route(services);
            ManualBackupHosting.Configure(services);
        });
        var server = api.Server;
        var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        var completion = server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var hostedBudget = server.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
        var streams = new List<HttpResponseMessage>();
        var readers = new List<StreamReader>();
        using var channel = PeerChannel.Create(server.BaseAddress);
        var peers = new List<AsyncDuplexStreamingCall<PushFrame, AckFrame>>();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            for (var index = 0; index < 4; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api.DatabasePath() + "/changes?cursor=now", UriKind.Relative));
                var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                streams.Add(response);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
                readers.Add(reader);
                await ReadEventAsync(reader, "cursor", token);
            }

            for (var index = 0; index < 2; index++)
            {
                var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: token);
                peers.Add(call);
                await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = ReplicaId.Of(123, (uint)index + 1, 0).Value, Region = "sjc" } }, token);
                await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
                await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
            }

            var text = new Faker().Lorem.Sentence();
            (await api.PutAsync(DocumentApi.NewId(), new { text })).Status.ShouldBe(HttpStatusCode.Created);
            foreach (var reader in readers)
            {
                await ReadEventAsync(reader, "change", token);
            }

            await server.Services.GetRequiredService<ContinuousBackup>().RunOnceAsync(token);
            var committed = DocumentApi.NewId();
            (await api.PutAsync(committed, new { text })).Status.ShouldBe(HttpStatusCode.Created);
            if (stalledBackup)
            {
                bucket.BeforePut = async (key, cancellation) =>
                {
                    if (lifetime.ApplicationStopping.IsCancellationRequested && key.Contains("/wal/", StringComparison.Ordinal))
                    {
                        blocked.TrySetResult();
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                        }
                        finally
                        {
                            cancelled.TrySetResult();
                        }
                    }
                };
            }

            // Keep clients alive throughout shutdown: client disposal cannot make the drain pass.
            var elapsed = Stopwatch.StartNew();
            lifetime.StopApplication();
            await completion.WaitAsync(TimeSpan.FromSeconds(45), token);
            TestContext.Current!.OutputWriter.WriteLine($"shutdown_seconds={elapsed.Elapsed.TotalSeconds:F3} stalled_backup={stalledBackup}");
            await Assert.That(elapsed.Elapsed).IsLessThan(Shutdown.Budget);
            await Assert.That(hostedBudget).IsEqualTo(Shutdown.HostedBudget);
            await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
            await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
            if (stalledBackup)
            {
                await Assert.That(blocked.Task.IsCompleted).IsTrue();
                await Assert.That(cancelled.Task.IsCompleted).IsTrue();
            }

            foreach (var reader in readers)
            {
                while (await reader.ReadLineAsync(token) is not null)
                {
                }
            }

            foreach (var peer in peers)
            {
                try
                {
                    await Assert.That(await peer.ResponseStream.MoveNext(token)).IsFalse();
                }
                catch (RpcException error) when (error.StatusCode is StatusCode.Cancelled or StatusCode.Unknown or StatusCode.Unavailable
                    || error.StatusCode == StatusCode.Internal && error.Status.DebugException is HttpProtocolException)
                {
                    // gRPC reports the server-side end while the client itself remains uncancelled.
                }
            }

            await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = server.DataDirectory);
            using var saved = await restarted.Client.GetAsync(new Uri(api.Document(committed), UriKind.Relative), token);
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
        finally
        {
            foreach (var peer in peers)
            {
                peer.Dispose();
            }

            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    private static async Task ReadEventAsync(StreamReader reader, string expected, CancellationToken cancellationToken)
    {
        var found = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            found |= line == "event: " + expected;
            if (line.Length == 0)
            {
                await Assert.That(found).IsTrue();
                return;
            }
        }

        throw new EndOfStreamException("changes stream ended before its event");
    }

    private sealed class HandshakeProbe : Interceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Entered.TrySetResult();
            return continuation(requestStream, responseStream, context);
        }
    }
}
