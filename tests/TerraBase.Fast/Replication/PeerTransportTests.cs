using System.Net;
using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Storage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class PeerTransportTests
{
    [Test]
    public async Task Sync_requires_Hello_first()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);

        using var call = client.Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { KeepAlive = new KeepAlive { Nonce = 7 } });
        await call.RequestStream.CompleteAsync();
        var error = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None))
            .Throws<RpcException>();

        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task Sync_refuses_a_Hello_without_LZ4()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var hello = ReplicationProtocol.CreateHello(Database(), RandomNumberGenerator.GetBytes(16));
        hello.Codecs.Clear();
        hello.Codecs.Add(PayloadCodec.Identity);

        using var call = client.Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello });
        await call.ResponseStream.MoveNext(CancellationToken.None);

        await Assert.That(call.ResponseStream.Current.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorIncompatibleVersion);
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task A_node_refuses_Sync_streams_beyond_its_limit(bool holdEcho, bool failEcho)
    {
        using var tls = new PeerTls();
        var echo = new HeldKeepAliveEcho { FailureNonce = failEcho ? ReplicationProtocol.MaxOpenStreams : null };
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
        {
            if (holdEcho)
            {
                services.AddSingleton(echo);
                services.AddGrpc(options => options.Interceptors.Add<HeldKeepAliveEcho>());
            }
        });
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var hellos = new List<SyncRequest>();
        for (var i = 0; i <= ReplicationProtocol.MaxOpenStreams; i++)
        {
            hellos.Add(new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) });
        }

        var testCancellation = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        var calls = new List<AsyncDuplexStreamingCall<SyncRequest, SyncResponse>>();
        var answers = new List<SyncResponse>();
        var maintenance = new List<Task>();
        Task<bool>? heldRead = null;
        Exception? primaryFailure = null;
        try
        {
            for (var i = 0; i <= ReplicationProtocol.MaxOpenStreams; i++)
            {
                var call = client.Sync(cancellationToken: testCancellation);
                calls.Add(call);
                try
                {
                    await call.RequestStream.WriteAsync(hellos[i], testCancellation);
                }
                catch (RpcException error) when (i == ReplicationProtocol.MaxOpenStreams && error.StatusCode == StatusCode.Cancelled)
                {
                    // Admission rejection can finish the response before Hello finishes writing.
                    // The response below must still contain the exact retryable rejection.
                }

                await Assert.That(await call.ResponseStream.MoveNext(testCancellation)).IsTrue();
                answers.Add(call.ResponseStream.Current);
                if (i < ReplicationProtocol.MaxOpenStreams)
                {
                    await Assert.That(answers[^1].Welcome).IsNotNull();
                    maintenance.Add(KeepAliveAsync(call, (ulong)i + 1));
                }
            }

            await Assert.That(answers.Take((int)ReplicationProtocol.MaxOpenStreams).All(answer => answer.Welcome is not null)).IsTrue();
            await Assert.That(answers[^1].Error).IsNotNull();
            await Assert.That(answers[^1].Error.Code).IsEqualTo(StreamErrorCode.StreamErrorResourceExhausted);
            await Assert.That(answers[^1].Error.Retryable).IsTrue();
            if (holdEcho)
            {
                // Cleanup must interrupt a real pending echo, rather than wait for the test deadline.
                echo.Hold = true;
                if (failEcho)
                {
                    var error = await Assert.ThrowsAsync<RpcException>(WaitForHeldEchoAsync);
                    await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.DataLoss);
                    await Assert.That(error.Status.Detail).IsEqualTo("injected keep-alive failure");
                    await Assert.That(echo.Pending.Task.IsCompleted).IsFalse();
                    await Assert.That(maintenance.Take(maintenance.Count - 1).All(task => !task.IsCompleted)).IsTrue();
                    // Observe the expected fault without hiding failures from any other cleanup task.
                    maintenance[^1] = ObserveExpectedFailureAsync(maintenance[^1], error);
                    await maintenance[^1];
                }
                else
                {
                    await WaitForHeldEchoAsync();
                }

                await Assert.That(echo.Held.Task.IsCompletedSuccessfully).IsTrue();
                heldRead = await echo.Held.Task;
                await Assert.That(heldRead.IsCompleted).IsFalse();
            }
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                await StoragePoolTests.JoinCleanupAsync(maintenance.Append(stop.CancelAsync()), primaryFailure);
            }
            finally
            {
                calls.ForEach(call => call.Dispose());
            }
        }

        if (heldRead is not null)
        {
            await Assert.That(heldRead.IsCompleted).IsTrue();
        }

        async Task WaitForHeldEchoAsync()
        {
            var completed = await Task.WhenAny(maintenance.Append(echo.Pending.Task))
                .WaitAsync(TimeSpan.FromSeconds(30), testCancellation);
            await completed;
            if (completed != echo.Pending.Task)
            {
                throw new InvalidOperationException("keep-alive maintenance ended before an echo was held");
            }
        }

        static async Task ObserveExpectedFailureAsync(Task failed, RpcException expected)
        {
            var actual = await Assert.ThrowsAsync<RpcException>(() => failed);
            await Assert.That(actual).IsSameReferenceAs(expected);
        }

        async Task KeepAliveAsync(AsyncDuplexStreamingCall<SyncRequest, SyncResponse> call, ulong nonce)
        {
            // Keep every accepted slot occupied while later handshakes run, and drain the protocol's echoes.
            using var timer = new PeriodicTimer(ReplicationProtocol.IdleTimeout / 3);
            try
            {
                do
                {
                    // Publish before sending so a held server write can identify this exact response read.
                    var read = holdEcho ? new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                    Volatile.Write(ref echo.Reads[(int)nonce - 1], read);
                    await WaitForIoAsync(call.RequestStream.WriteAsync(new SyncRequest { KeepAlive = new KeepAlive { Nonce = nonce } }, testCancellation));
                    stop.Token.ThrowIfCancellationRequested();
                    var next = call.ResponseStream.MoveNext(testCancellation);
                    read?.TrySetResult(next);
                    await WaitForIoAsync(next);
                    await Assert.That(await next).IsTrue();
                    await Assert.That(call.ResponseStream.Current.FrameCase).IsEqualTo(SyncResponse.FrameOneofCase.KeepAlive);
                    await Assert.That(call.ResponseStream.Current.KeepAlive.Nonce).IsEqualTo(nonce);
                }
                while (await timer.WaitForNextTickAsync(stop.Token));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && testCancellation.IsCancellationRequested)
            {
            }

            async Task WaitForIoAsync(Task pending)
            {
                try
                {
                    await pending.WaitAsync(stop.Token);
                }
                catch (OperationCanceledException error) when (error.CancellationToken == stop.Token && stop.IsCancellationRequested)
                {
                    // Preserve completed I/O even when local cancellation won the wait.
                    if (pending.IsCompleted)
                    {
                        await pending;
                        return;
                    }

                    call.Dispose();
                    try
                    {
                        await pending;
                        return;
                    }
                    catch (Exception aborted) when (IsLocalAbort(aborted))
                    {
                    }
                    catch (Exception aborted)
                    {
                        // A gRPC upgrade can change the local-abort shape; name it rather than fail as a generic cleanup error.
                        throw new InvalidOperationException("unrecognized gRPC abort shape or transport failure after local disposal", aborted);
                    }

                    throw;
                }
            }

            bool IsLocalAbort(Exception error)
            {
                return call.GetStatus() is { StatusCode: StatusCode.Cancelled, Detail: "gRPC call disposed." } && (error switch
                {
                    RpcException { StatusCode: StatusCode.Cancelled, Status.Detail: "gRPC call disposed." } => true,
                    // gRPC 2.84 maps HTTP/2's local abort IOException to Unavailable; a transport reset has an inner cause.
                    RpcException { StatusCode: StatusCode.Unavailable, Status.DebugException: IOException { Message: "The request was aborted.", InnerException: null } io } => io.GetType() == typeof(IOException),
                    IOException { Message: "The request was aborted.", InnerException: null } io => io.GetType() == typeof(IOException),
                    _ => false,
                });
            }
        }
    }

    private sealed class HeldKeepAliveEcho : Interceptor
    {
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Task<bool>> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Task<bool>>?[] Reads { get; } = new TaskCompletionSource<Task<bool>>?[(int)ReplicationProtocol.MaxOpenStreams];

        public ulong? FailureNonce { get; init; }

        public bool Hold
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
            continuation(requestStream, new HeldWriter<TResponse>(responseStream, this, context.CancellationToken), context);

        private sealed class HeldWriter<T>(IServerStreamWriter<T> inner, HeldKeepAliveEcho echo, CancellationToken cancellationToken) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions
            {
                get => inner.WriteOptions;
                set => inner.WriteOptions = value;
            }

            public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

            public async Task WriteAsync(T message, CancellationToken writeCancellation)
            {
                if (echo.Hold && message is SyncResponse { FrameCase: SyncResponse.FrameOneofCase.KeepAlive } response)
                {
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeCancellation);
                    if (response.KeepAlive.Nonce == echo.FailureNonce)
                    {
                        // Fail only after another response write is held and its client read is outstanding.
                        await echo.Held.Task.WaitAsync(stop.Token);
                        throw new RpcException(new Status(StatusCode.DataLoss, "injected keep-alive failure"));
                    }

                    var nonce = response.KeepAlive.Nonce;
                    if (nonce is 0 or > ReplicationProtocol.MaxOpenStreams || Volatile.Read(ref echo.Reads[(int)nonce - 1]) is not { } published)
                    {
                        throw new InvalidOperationException($"keep-alive nonce {nonce} has no published client read");
                    }

                    var read = await published.Task.WaitAsync(stop.Token);
                    var held = Task.Delay(Timeout.Infinite, stop.Token);
                    echo.Held.TrySetResult(read);
                    if (echo.FailureNonce is null)
                    {
                        // Keep the success signal unset when maintenance must win the fault race.
                        echo.Pending.TrySetResult();
                    }

                    await held;
                }

                await inner.WriteAsync(message, writeCancellation);
            }
        }
    }

    [Test]
    public async Task Eventually_cancels_an_active_response_read_before_returning()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        var ioCancelled = false;
        var conditionFinished = false;

        var polling = TerraBaseTestServer.EventuallyAsync(async token =>
        {
            try
            {
                // The real stream stays open, with no next response until another request arrives.
                return await call.ResponseStream.MoveNext(token);
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
            {
                ioCancelled = true;
                token.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                conditionFinished = true;
            }
        }, TimeSpan.FromMilliseconds(200));
        // Observe completion inline, so a condition finishing after the helper returns cannot pass by racing the assertion.
        var finishedAtReturn = polling.ContinueWith(_ => ioCancelled && conditionFinished,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        await Assert.ThrowsAsync<OperationCanceledException>(() => polling);
        await Assert.That(await finishedAtReturn).IsTrue();
    }

    [Test]
    public async Task Eventually_rejects_a_true_result_returned_after_its_deadline()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var clock = new PollingClock();
        var ioFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conditionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polling = TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            response.EnsureSuccessStatusCode();
            ioFinished.TrySetResult();
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => expired.TrySetResult());
            await expired.Task;
            conditionFinished.TrySetResult();
            return true;
        }, TimeSpan.FromSeconds(1), clock);
        try
        {
            await ioFinished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Expire();
            await conditionFinished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAsync<OperationCanceledException>(() => polling);
        }
        finally
        {
            clock.Expire();
            try
            {
                await polling;
            }
            catch (OperationCanceledException)
            {
                // Join the condition even if a completion barrier or assertion fails.
            }
        }
    }

    private sealed class PollingClock : TimeProvider
    {
        private ITimer? Deadline { get; set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Deadline = base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);

        public void Expire()
        {
            // The probe has one deadline; release it only after the real request completes.
            var deadline = Deadline ?? throw new InvalidOperationException("the polling deadline has not started");
            _ = deadline.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    [Test]
    public async Task Hello_must_include_protocol_1_0_and_a_consistent_identity_and_limits_are_negotiated()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);

        var otherOnly = await FirstResponseAsync(tls, server, hello =>
            hello.ProtocolVersions = new VersionRange { Minimum = new ProtocolVersion { Major = 2 }, Maximum = new ProtocolVersion { Major = 2 } });
        var newerOnly = await FirstResponseAsync(tls, server, hello =>
            hello.ProtocolVersions = new VersionRange { Minimum = new ProtocolVersion { Major = ReplicationProtocol.Major, Minor = 1 }, Maximum = new ProtocolVersion { Major = ReplicationProtocol.Major, Minor = 2 } });
        var otherMember = await FirstResponseAsync(tls, server, hello =>
            hello.LocalIdentity.MemberId = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)));
        var noCredit = await FirstResponseAsync(tls, server, hello => hello.Limits.ByteCredit = 0);
        var otherEpoch = await FirstResponseAsync(tls, server, hello => hello.LocalIdentity.MembershipEpoch = 7);
        var smaller = await FirstResponseAsync(tls, server, hello =>
        {
            hello.Limits.ByteCredit = 1024 * 1024;
            hello.Limits.MaxInFlightBatches = 2;
        });

        await Assert.That(otherOnly.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorIncompatibleVersion);
        await Assert.That(newerOnly.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorIncompatibleVersion);
        await Assert.That(otherMember.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorUnsupportedFormat);
        await Assert.That(noCredit.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorIncompatibleVersion);
        await Assert.That(otherEpoch.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorStaleEpoch);
        await Assert.That(smaller.Welcome.Limits.ByteCredit).IsEqualTo(1024UL * 1024);
        await Assert.That(smaller.Welcome.Limits.MaxInFlightBatches).IsEqualTo(2U);
        await Assert.That(smaller.Welcome.Limits.MaxDecodedApplicationBytes).IsEqualTo((uint)ReplicationProtocol.MaxApplicationBytes);
    }

    [Test]
    [Arguments("tenant", false)]
    [Arguments("tenant", true)]
    [Arguments("project", false)]
    [Arguments("project", true)]
    public async Task Hello_refuses_missing_or_malformed_tenant_and_project_ULIDs(string field, bool malformed)
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var database = Database();
        var hello = ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16));
        var invalid = malformed ? Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(15)) : Google.Protobuf.ByteString.Empty;
        if (field == "tenant")
        {
            hello.LocalIdentity.TenantId = invalid;
        }
        else
        {
            hello.LocalIdentity.ProjectId = invalid;
        }

        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorUnsupportedFormat);
        await Assert.That(call.ResponseStream.Current.Error.Retryable).IsFalse();
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(server.DataDirectory, database.OrganizationId))).IsFalse();
    }

    [Test]
    public async Task Listeners_stay_separate()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var publicHttp = new HttpClient { BaseAddress = server.ClientAddress };
        using var metricsHttp = new HttpClient { BaseAddress = server.MetricsAddress };

        using var grpcOnPublic = await publicHttp.PostAsync(new Uri("/terrabase.v1.Replication/Sync", UriKind.Relative), new ByteArrayContent([]));
        using var adminOnPublic = await publicHttp.GetAsync(new Uri("/replication", UriKind.Relative));
        using var metricsOnPublic = await publicHttp.GetAsync(new Uri("/metrics", UriKind.Relative));
        using var healthOnMetrics = await metricsHttp.GetAsync(new Uri("/healthz", UriKind.Relative));
        using var metrics = await metricsHttp.GetAsync(new Uri("/metrics", UriKind.Relative));
        using var adminOnInternal = await server.Client.GetAsync(new Uri("/replication", UriKind.Relative));

        await Assert.That(grpcOnPublic.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(adminOnPublic.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(metricsOnPublic.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(healthOnMetrics.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(metrics.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(adminOnInternal.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // The first response to a Hello edited by `edit`.
    private static async Task<SyncResponse> FirstResponseAsync(PeerTls tls, TerraBaseTestServer server, Action<Hello> edit)
    {
        var hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16));
        edit(hello);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello });
        await call.ResponseStream.MoveNext(CancellationToken.None);
        return call.ResponseStream.Current;
    }

    // Whether a fresh connection that trusts only this PKI's CA and presents its client certificate (or
    // `clientCertificate`) gets a Welcome.
    private static TenantContext Database() =>
        new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
}
