using System.Security.Cryptography;
using Bogus;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Tenancy;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

public sealed class StreamCancellationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Caller_cancellation_releases_the_receiver_without_reporting_a_peer_idle_timeout(bool pauseBeforeRead)
    {
        using var tls = new PeerTls();
        var evidence = new CancellationEvidence(pauseBeforeRead);
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
        {
            services.AddSingleton(evidence);
            services.AddGrpc(options => options.Interceptors.Add<CancellationEvidence>());
        });
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var fake = new Faker();
        var database = new TenantContext(fake.Random.Guid().ToString("D"), PlanTier.Hobby,
            fake.Random.Guid().ToString("D"), fake.Random.Guid().ToString("D"));
        var hello = ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using (var call = client.Sync(cancellationToken: cancellation.Token))
        {
            await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello }, deadline.Token);
            await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
            if (pauseBeforeRead)
            {
                await evidence.Waiting.Task.WaitAsync(deadline.Token);
            }

            cancellation.Cancel();
            var error = await Assert.That(async () => await call.ResponseStream.MoveNext(deadline.Token)).Throws<RpcException>();
            await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.Cancelled);
        }

        // The client status alone cannot detect server misclassification; observe the real service's exception.
        var (failure, parentCancelled) = await evidence.Completed.Task.WaitAsync(deadline.Token);
        if (pauseBeforeRead)
        {
            await Assert.That(parentCancelled).IsTrue();
            await Assert.That(failure is OperationCanceledException).IsTrue();
        }
        else
        {
            // Kestrel can surface the HTTP/2 reset before the parent token is marked canceled.
            await Assert.That(failure is null or IOException or OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled }).IsTrue();
        }
        using var next = client.Sync(cancellationToken: deadline.Token);
        await next.RequestStream.WriteAsync(new SyncRequest { Hello = hello }, deadline.Token);
        await Assert.That(await next.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        await Assert.That(next.ResponseStream.Current.Welcome).IsNotNull();
        await next.RequestStream.CompleteAsync();
        await Assert.That(await next.ResponseStream.MoveNext(deadline.Token)).IsFalse();
    }

    private sealed class CancellationEvidence(bool pauseBeforeRead) : Interceptor
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(Exception? Error, bool ParentCancelled)> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Exception? failure = null;
            try
            {
                await continuation(new GatedReader<TRequest>(requestStream, this, pauseBeforeRead), responseStream, context);
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

        private sealed class GatedReader<T>(IAsyncStreamReader<T> reader, CancellationEvidence evidence, bool pause) : IAsyncStreamReader<T>
        {
            private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _reads;

            public T Current => reader.Current;

            public async Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                if (pause && _reads++ == 1 && !evidence.Completed.Task.IsCompleted)
                {
                    // Gate only scheduling: Hello and every request still come from the real HTTP/2 reader.
                    // Cancellation reaches the service's token-aware wait before a transport-reset IOException can win.
                    evidence.Waiting.TrySetResult();
                    await _resume.Task.WaitAsync(cancellationToken);
                }

                return await reader.MoveNext(cancellationToken);
            }
        }
    }
}
