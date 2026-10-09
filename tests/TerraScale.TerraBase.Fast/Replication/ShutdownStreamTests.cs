using System.Security.Cryptography;
using Bogus;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class ShutdownStreamTests
{
    [Test]
    public async Task Shutdown_ends_an_idle_catalog_Follow_before_Kestrel_stops()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var tls = new PeerTls();
        var evidence = new StreamCompletion();
        var shutdown = new AwaitStreamCompletion(evidence, token);
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
        {
            services.AddSingleton(evidence);
            services.AddGrpc(options => options.Interceptors.Add<StreamCompletion>());
            services.AddHostedService(_ => shutdown);
        });
        try
        {
            using var channel = tls.CreateChannel(server.PeerAddress!);
            using var call = new CatalogReplication.CatalogReplicationClient(channel).Follow(new FollowRequest
            {
                Protocol = CatalogProtocol.Version,
                Codecs = { PayloadCodec.Lz4BlockV1 },
                Region = "test-follower",
            }, cancellationToken: token);
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
            do
            {
                await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            }
            while (call.ResponseStream.Current.FrameCase != FollowResponse.FrameOneofCase.Heartbeat);
            await evidence.IdleFollow.Task.WaitAsync(token);

            await AssertShutdownAsync(server, call.ResponseStream, evidence, shutdown, token);
        }
        finally
        {
            shutdown.Release.TrySetResult();
        }
    }

    [Test]
    public async Task Shutdown_ends_an_idle_replication_Sync_before_Kestrel_stops()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var tls = new PeerTls();
        var evidence = new StreamCompletion();
        var shutdown = new AwaitStreamCompletion(evidence, token);
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
        {
            services.AddSingleton(evidence);
            services.AddGrpc(options => options.Interceptors.Add<StreamCompletion>());
            services.AddHostedService(_ => shutdown);
        });
        try
        {
            var fake = new Faker();
            var database = await server.RegisterAsync(new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby,
                EntityIds.New().ToString(), EntityIds.New().ToString()));
            using var channel = tls.CreateChannel(server.PeerAddress!);
            using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: token);
            await call.RequestStream.WriteAsync(new SyncRequest
            {
                Hello = ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16)),
            }, token);
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
            await evidence.IdleRead.Task.WaitAsync(token);

            // Leave the request side open: the service must cancel its idle read, not observe a client half-close.
            await AssertShutdownAsync(server, call.ResponseStream, evidence, shutdown, token);
        }
        finally
        {
            shutdown.Release.TrySetResult();
        }
    }

    private static async Task AssertShutdownAsync<T>(
        TerraBaseTestServer server, IAsyncStreamReader<T> responses, StreamCompletion evidence,
        AwaitStreamCompletion shutdown, CancellationToken cancellationToken)
    {
        var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        await Assert.That(evidence.Completed.Task.IsCompleted).IsFalse();
        lifetime.StopApplication();
        await shutdown.StreamEnded.Task.WaitAsync(cancellationToken);
        var (error, callerCancelled) = await evidence.Completed.Task.WaitAsync(cancellationToken);
        await Assert.That(lifetime.ApplicationStopping.IsCancellationRequested).IsTrue();
        await Assert.That(lifetime.ApplicationStopped.IsCancellationRequested).IsFalse();
        await Assert.That(callerCancelled).IsFalse();
        await Assert.That(error is null or OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled }).IsTrue();

        // StoppingAsync still holds Kestrel: the client must receive the service's end, not a shutdown abort.
        try
        {
            await Assert.That(await responses.MoveNext(cancellationToken)).IsFalse();
        }
        catch (RpcException failure) when (failure.StatusCode is StatusCode.Cancelled or StatusCode.Unknown)
        {
            // gRPC can map the service's cancellation to either status while its caller is still connected.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class StreamCompletion : Interceptor
    {
        public TaskCompletionSource IdleFollow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource IdleRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(Exception? Error, bool CallerCancelled)> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
            TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            await ObserveAsync(() => continuation(request, new ObservedWriter<TResponse>(responseStream, IdleFollow), context), context);
        }

        public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            await ObserveAsync(() => continuation(new ObservedReader<TRequest>(requestStream, IdleRead), responseStream, context), context);
        }

        private async Task ObserveAsync(Func<Task> continuation, ServerCallContext context)
        {
            Exception? failure = null;
            try
            {
                await continuation();
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                Completed.TrySetResult((failure, context.CancellationToken.IsCancellationRequested));
            }
        }

        private sealed class ObservedReader<T>(IAsyncStreamReader<T> reader, TaskCompletionSource idle) : IAsyncStreamReader<T>
        {
            private int _reads;

            public T Current => reader.Current;

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                var pending = reader.MoveNext(cancellationToken);
                if (++_reads == 2)
                {
                    idle.TrySetResult();
                }

                return pending;
            }
        }

        private sealed class ObservedWriter<T>(IServerStreamWriter<T> writer, TaskCompletionSource idle) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions
            {
                get => writer.WriteOptions;
                set => writer.WriteOptions = value;
            }

            public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

            public async Task WriteAsync(T message, CancellationToken cancellationToken)
            {
                await writer.WriteAsync(message, cancellationToken);
                if (message is FollowResponse { FrameCase: FollowResponse.FrameOneofCase.Heartbeat })
                {
                    // Signal after gRPC disposed its write-cancellation registration, so shutdown cancels the idle wait.
                    idle.TrySetResult();
                }
            }
        }
    }

    // All StoppingAsync callbacks finish before any hosted service's StopAsync, including Kestrel's.
    private sealed class AwaitStreamCompletion(StreamCompletion evidence, CancellationToken testCancellation) : IHostedLifecycleService
    {
        public TaskCompletionSource StreamEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StoppingAsync(CancellationToken cancellationToken)
        {
            if (Release.Task.IsCompleted)
            {
                // Setup failed before requesting shutdown; let normal teardown clean up the test's server.
                return;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, testCancellation);
            await evidence.Completed.Task.WaitAsync(deadline.Token);
            StreamEnded.TrySetResult();
            await Release.Task.WaitAsync(deadline.Token);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
