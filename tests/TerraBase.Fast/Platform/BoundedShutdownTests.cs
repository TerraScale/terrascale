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
using TerraBase.Engine.Turso;
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
    public async Task Shutdown_rejects_new_work_while_health_probes_keep_their_response()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var held = new HeldShutdown();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddHostedService(_ => held));
        using var healthy = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
        await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await held.Stopping.Task.WaitAsync(token);
        try
        {
            using var refused = await server.Client.GetAsync(new Uri("/v1/databases", UriKind.Relative), token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var probe = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await probe.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
        }
        finally
        {
            held.Release.TrySetResult();
        }
    }

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
        using var channel = PeerChannel.Create(server.BaseAddress, PeerTls.Credential);
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
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var hostedBudget = server.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
        var streams = new List<HttpResponseMessage>();
        var readers = new List<StreamReader>();
        using var channel = PeerChannel.Create(server.BaseAddress, PeerTls.Credential);
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

    [Test]
    public async Task A_changes_page_failed_by_the_host_stop_ends_its_stream_cleanly()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync();
        var server = api.Server;
        var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        var completion = server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
        var tenant = await api.TenantOfAsync(cancellationToken: token);
        var store = (await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, token))!;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api.DatabasePath() + "/changes?cursor=now", UriKind.Relative));
        using var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        await ReadEventAsync(reader, "cursor", token);

        // Stopping callbacks run newest first: this one runs before the stream's older linked stop token sees the stop. A
        // commit here makes the stream read a page whose producer, bound to the host stop, fails before that token does.
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = Task.Run(async () =>
        {
            await stopping.Task;
            await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                _ = await write.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), JsonSerializer.SerializeToUtf8Bytes(new { text = new Faker().Lorem.Sentence() }), CancellationToken.None);
                _ = await write.CommitAsync(CancellationToken.None);
            }

            try
            {
                while (await reader.ReadLineAsync(CancellationToken.None) is not null)
                {
                }

                return null;
            }
            catch (HttpIOException error)
            {
                return error;
            }
        }, token);
        using (lifetime.ApplicationStopping.Register(() =>
        {
            stopping.TrySetResult();
            _ = ended.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
        }))
        {
            lifetime.StopApplication();
        }

        await completion.WaitAsync(TimeSpan.FromSeconds(45), token);
        await Assert.That(ended.IsCompleted).IsTrue().Because("the page failed by the stop ends the stream before the stop reaches it");
        await Assert.That(await ended).IsNull().Because("a stream the host stops ends with EOF, not an aborted response");
    }

    [Test]
    public async Task A_healthy_reader_can_retire_its_intent_after_the_shutdown_grace_expires()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(settings =>
        {
            settings["shutdown-grace-seconds"] = "1";
        });
        var directory = api.Server.DataDirectory;
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var replica = engine.ReplicaId;
        var tenant = await api.TenantOfAsync();
        var store = (await engine.OpenDatabaseAsync(tenant, token))!;
        await using var reader = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, id, token)).IsNotNull();
        try
        {
            // The real grace wait expires while this admitted reader keeps native cleanup outstanding.
            await engine.DisposeAsync();
            await Assert.That(engine.Io.Closed).IsTrue();
            await Assert.That(engine.CleanupCompletion.IsCompleted).IsFalse();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsTrue();
        }
        finally
        {
            // Retiring the last reader still needs the journal writer and the I/O pool.
            await reader.DisposeAsync();
        }

        await engine.CleanupCompletion.WaitAsync(token);
        await Assert.That(engine.Ledger.FailedClosed).IsNull();
        await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
        await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        var reopened = restarted.Services.GetRequiredService<TursoStorageEngine>();
        await Assert.That(reopened.ReplicaId).IsEqualTo(replica).Because("healthy reader cleanup must record a clean stop");
        var saved = (await reopened.OpenDatabaseAsync(tenant, token))!;
        await using var check = await saved.BeginTransactionAsync(write: false, token);
        await Assert.That(await check.GetAsync(DocumentApi.Collection, id, token)).IsNotNull();
        await Assert.That(await reopened.Ledger.GetAsync(tenant, token)).IsNull();
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

    private sealed class HeldShutdown : IHostedService
    {
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Stopping.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
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
