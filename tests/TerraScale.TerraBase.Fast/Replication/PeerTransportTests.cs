using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bogus;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
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
    public async Task An_application_rejection_is_not_evidence_of_TLS_rejection()
    {
        using var tls = new PeerTls();
        await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            await responses.WriteAsync(new SyncResponse
            {
                Error = new StreamError { Code = StreamErrorCode.StreamErrorResourceExhausted },
            });
        });
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(Database(), RandomNumberGenerator.GetBytes(16)) };

        await Assert.ThrowsAsync<InvalidDataException>(() => HandshakeAsync(tls, peer.Address, hello));
    }

    [Test]
    public async Task An_authenticated_connection_aborted_before_HTTP2_is_not_evidence_of_TLS_rejection()
    {
        using var tls = new PeerTls();
        using var evidence = new TlsEvidence();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, evidence.Register);
        using var handler = tls.CreateHandler();
        var mutuallyAuthenticated = false;
        handler.PlaintextStreamFilter = async (context, token) =>
        {
            mutuallyAuthenticated = context.PlaintextStream is SslStream { IsMutuallyAuthenticated: true };
            // Both ends finished TLS. Stop the real listener before the client starts HTTP/2.
            await evidence.Authentication.WaitAsync(token);
            await server.Services.GetRequiredService<IServer>().StopAsync(new CancellationToken(canceled: true));
            // Drain frames already buffered at shutdown so HTTP/2 observes EOF/reset before its initial SETTINGS.
            try
            {
                await context.PlaintextStream.CopyToAsync(Stream.Null, 1024, token);
            }
            catch (IOException)
            {
                // An aborted TLS connection may reset instead of returning EOF; the next read still fails.
            }

            return context.PlaintextStream;
        };
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(Database(), RandomNumberGenerator.GetBytes(16)) };

        var error = await Assert.That(() => HandshakeAsync(tls, server.PeerAddress!, hello, evidence: evidence, handler: handler))
            .ThrowsException();

        await Assert.That(mutuallyAuthenticated).IsTrue();
        await Assert.That(await evidence.Authentication.WaitAsync(TimeSpan.FromSeconds(30))).IsNull();
        await Assert.That(Causes(error!).Any(cause => cause is HttpIOException { HttpRequestError: HttpRequestError.InvalidResponse })).IsTrue();
        await Assert.That(IsTlsRejection(error!, authenticationRejected: false)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Peer_certificates_reload_without_a_restart(bool preserveTimestamps)
    {
        var clock = new PeerCredentialClock();
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        using var evidence = new TlsEvidence();
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services => { evidence.Register(services); services.AddSingleton<TimeProvider>(clock); });
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) };
        var files = server.Services.GetRequiredService<TerraBaseOptions>().PeerTls!;
        var paths = new[] { files.Certificate, files.PrivateKey, files.Authority };
        var timestamps = paths.Select(File.GetLastWriteTimeUtc).ToArray();

        var before = await HandshakeAsync(rotated, server.PeerAddress!, hello, evidence: evidence);
        original.OverwriteWith(rotated, preserveTimestamps);
        clock.Advance();
        if (preserveTimestamps)
        {
            await Assert.That(paths.Select(File.GetLastWriteTimeUtc).SequenceEqual(timestamps)).IsTrue();
        }

        var after = await HandshakeAsync(rotated, server.PeerAddress!, hello, evidence: evidence);
        var stale = await HandshakeAsync(original, server.PeerAddress!, hello, evidence: evidence);
        // Trusts the new server certificate but presents a client certificate of the removed CA.
        var staleClient = await HandshakeAsync(rotated, server.PeerAddress!, hello, original.ClientCertificate, evidence);

        await Assert.That(before).IsFalse();
        await Assert.That(after).IsTrue();
        await Assert.That(stale).IsFalse();
        await Assert.That(staleClient).IsFalse();
        await Assert.That(await evidence.Authentication.WaitAsync(TimeSpan.FromSeconds(30)) is AuthenticationException).IsTrue();
    }

    [Test]
    public async Task An_inbound_handshake_keeps_its_certificate_authorities_when_files_rotate_after_certificate_selection()
    {
        var clock = new PeerCredentialClock();
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        using var evidence = new TlsEvidence();
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services => { evidence.Register(services); services.AddSingleton<TimeProvider>(clock); });
        using var handler = original.CreateHandler();
        var replaced = false;
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12;
        handler.SslOptions.ClientCertificates = null;
        handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, remoteCertificate, _) =>
        {
            if (remoteCertificate is null)
            {
                return null!;
            }

            // The old server leaf is selected; no client certificate has been sent for validation yet.
            original.OverwriteWith(rotated, preserveTimestamps: true);
            clock.Advance();
            replaced = true;
            return original.ClientCertificate;
        };
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) };

        await Assert.That(await HandshakeAsync(original, server.PeerAddress!, hello, evidence: evidence, handler: handler)).IsTrue();
        await Assert.That(replaced).IsTrue();
        await Assert.That(await evidence.Authentication.WaitAsync(TimeSpan.FromSeconds(30))).IsNull();
        clock.Advance();
        // A fresh connection observes the replacement; the old client is then rejected against the new CA.
        await Assert.That(await HandshakeAsync(rotated, server.PeerAddress!, hello, evidence: evidence)).IsTrue();
        await Assert.That(await HandshakeAsync(rotated, server.PeerAddress!, hello, original.ClientCertificate, evidence)).IsFalse();
        await Assert.That(await evidence.Authentication.WaitAsync(TimeSpan.FromSeconds(30)) is AuthenticationException).IsTrue();
    }

    [Test]
    public async Task A_sender_keeps_one_credential_pair_during_rotation_and_reloads_on_reconnection()
    {
        var clock = new PeerCredentialClock();
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        using var sourceTls = new PeerTls();
        using var clientTls = new ClientTls();
        sourceTls.OverwriteWith(original);
        using var oldCertificate = original.LoadNodeCertificate();
        using var newCertificate = rotated.LoadNodeCertificate();
        var connections = 0;
        var rotatedClient = false;
        var documentConnection = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = new Faker().Random.AlphaNumeric(32);
        var id = EntityIds.New().ToString();
        var directory = Directory.CreateTempSubdirectory("terrabase-outbound-rotation-").FullName;
        try
        {
            TenantContext database;
            // Persist the document before any peer attempt, so the first handshake cannot belong to an empty turn.
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = directory;
            }))
            {
                var tenant = await ReplicationTenant.CreateAsync(seed, "outbound-rotation");
                database = tenant.Database("outbound-rotation");
                using var client = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                using var created = await client.PutAsync(new Uri(tenant.Path("outbound-rotation"), UriKind.Relative), null);
                created.EnsureSuccessStatusCode();
                using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("outbound-rotation")}/collections", UriKind.Relative), new { name = "items" });
                collection.EnsureSuccessStatusCode();
                using var written = await client.PutAsJsonAsync(new Uri($"{tenant.Path("outbound-rotation")}/collections/items/documents/by-key?id={id}", UriKind.Relative), new { value });
                written.EnsureSuccessStatusCode();
            }
            await using var peer = await FakePeer.StartAsync(original, async (requests, responses, context) =>
            {
                if (!await requests.MoveNext(context.CancellationToken))
                {
                    return;
                }

                var hello = requests.Current.Hello;
                var connection = Volatile.Read(ref connections);
                await responses.WriteAsync(FakePeer.Welcome(hello, out var member));
                var receivedDocument = false;
                while (await requests.MoveNext(context.CancellationToken))
                {
                    if (requests.Current.CommittedGroup is not { } batch)
                    {
                        continue;
                    }

                    var changes = ReplicationProtocol.Read(requests.Current, database!, hello.LocalIdentity.MemberId.Memory, hello.Limits);
                    if (changes.Any(change => change.Id == id && change.Json is { } json
                        && Encoding.UTF8.GetString(json.Span).Contains(value, StringComparison.Ordinal)))
                    {
                        receivedDocument = true;
                        documentConnection.TrySetResult(connection);
                    }

                    await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence));
                }

                if (connection == 1 && receivedDocument)
                {
                    // The source completed its requests after receiving the ACK. Force its next stream to reconnect.
                    context.GetHttpContext().Features.Get<IConnectionLifetimeFeature>()!.Abort();
                }
            }, https =>
            {
                // TLS 1.2 validates the client before it sends the server's final flight to the client.
                https.SslProtocols = SslProtocols.Tls12;
                https.ServerCertificateSelector = (_, _) => Volatile.Read(ref connections) == 0 ? oldCertificate : newCertificate;
                https.ClientCertificateValidation = (certificate, _, _) =>
                {
                    if (Interlocked.Increment(ref connections) == 1)
                    {
                        if (certificate.Thumbprint != oldCertificate.Thumbprint)
                        {
                            return false;
                        }

                        // The source selected its old client leaf; it has yet to validate our old server leaf.
                        sourceTls.OverwriteWith(rotated, preserveTimestamps: true);
                        clock.Advance();
                        return true;
                    }

                    rotatedClient = certificate.Thumbprint == newCertificate.Thumbprint;
                    return rotatedClient;
                };
            });
            await using var source = await TerraBaseTestServer.StartAsync(settings =>
            {
                sourceTls.Apply(settings);
                clientTls.Apply(settings);
                settings["peer-addresses"] = peer.Address.ToString();
                settings["data-directory"] = directory;
            }, services =>
            {
                // This fake peer scripts only the journal protocol and owns the first TLS handshake.
                services.Remove(services.Single(service => service.ImplementationType == typeof(AntiEntropyWorker)));
                services.AddSingleton<TimeProvider>(clock);
            });

            await Assert.That(await documentConnection.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(1);
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                // Retry eligibility uses the injected monotonic clock too.
                clock.Advance();
                return Task.FromResult(Volatile.Read(ref connections) >= 2);
            });
            await TerraBaseTestServer.ReplicatedAsync(source);
            await Assert.That(rotatedClient).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_staged_rotation_that_fails_the_first_startup_load_is_retried()
    {
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        var loads = 0;
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            var files = options.PeerTls!;
            return new PeerCredentials(options, provider.GetRequiredService<ILogger<PeerCredentials>>(), () =>
            {
                if (++loads == 1)
                {
                    // Configuration was valid; the singleton now sees a new leaf with the old CA.
                    original.OverwriteCertificateWith(rotated);
                    try
                    {
                        return files.Load();
                    }
                    finally
                    {
                        original.OverwriteAuthorityWith(rotated);
                    }
                }

                return files.Load();
            });
        }));
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) };
        await Assert.That(loads).IsEqualTo(2);
        await Assert.That(await HandshakeAsync(rotated, server.PeerAddress!, hello)).IsTrue();
        await Assert.That(await HandshakeAsync(original, server.PeerAddress!, hello)).IsFalse();
    }

    [Test]
    public async Task A_rotation_during_credential_initialization_discards_the_old_pair()
    {
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        X509Certificate2? discarded = null;
        X509Certificate2Collection? discardedAuthorities = null;
        var loads = 0;
        using var evidence = new TlsEvidence();
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services =>
        {
            evidence.Register(services);
            services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<TerraBaseOptions>();
                var files = options.PeerTls!;
                return new PeerCredentials(options, provider.GetRequiredService<ILogger<PeerCredentials>>(), () =>
                {
                    var pair = files.Load();
                    if (++loads == 1)
                    {
                        discarded = pair.Certificate;
                        discardedAuthorities = pair.Authorities;
                        original.OverwriteWith(rotated, preserveTimestamps: true);
                    }

                    return pair;
                });
            });
        });
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) };

        await Assert.That(await HandshakeAsync(rotated, server.PeerAddress!, hello, evidence: evidence)).IsTrue();
        await Assert.That(await HandshakeAsync(original, server.PeerAddress!, hello, evidence: evidence)).IsFalse();
        // Trust the rotated server while presenting the old client, to check server-side trust too.
        await Assert.That(await HandshakeAsync(rotated, server.PeerAddress!, hello, original.ClientCertificate, evidence)).IsFalse();
        await Assert.That(await evidence.Authentication.WaitAsync(TimeSpan.FromSeconds(30)) is AuthenticationException).IsTrue();
        await Assert.That(loads).IsEqualTo(2);
        await Assert.That(discarded!.Handle).IsEqualTo(IntPtr.Zero);
        await Assert.That(discardedAuthorities!).IsNotEmpty();
        await Assert.That(discardedAuthorities!.Cast<X509Certificate2>().All(authority => authority.Handle == IntPtr.Zero)).IsTrue();
    }

    [Test]
    public async Task A_failed_final_fingerprint_read_disposes_the_load_and_keeps_the_previous_credentials()
    {
        var clock = new PeerCredentialClock();
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        var discarded = new List<(X509Certificate2 Certificate, X509Certificate2Collection Authorities, X509Certificate2Collection Intermediates)>();
        var failFinalRead = false;
        var loaded = false;
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            var files = options.PeerTls!;
            return new PeerCredentials(options, provider.GetRequiredService<ILogger<PeerCredentials>>(), () =>
            {
                var pair = files.Load();
                if (failFinalRead)
                {
                    discarded.Add(pair);
                    loaded = true;
                }

                return pair;
            }, () =>
            {
                if (loaded)
                {
                    loaded = false;
                    throw new IOException("final fingerprint read failed");
                }

                return (Fingerprint(files.Certificate), Fingerprint(files.PrivateKey), Fingerprint(files.Authority));
            }, time: clock);
        }));
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) };

        original.OverwriteWith(rotated, preserveTimestamps: true);
        failFinalRead = true;
        clock.Advance();
        await Assert.That(await HandshakeAsync(original, server.PeerAddress!, hello)).IsTrue();
        await Assert.That(discarded).IsNotEmpty();
        foreach (var (certificate, authorities, intermediates) in discarded)
        {
            await Assert.That(certificate.Handle).IsEqualTo(IntPtr.Zero);
            await Assert.That(authorities).IsNotEmpty();
            await Assert.That(authorities.Cast<X509Certificate2>().All(authority => authority.Handle == IntPtr.Zero)).IsTrue();
            await Assert.That(intermediates.Cast<X509Certificate2>().All(intermediate => intermediate.Handle == IntPtr.Zero)).IsTrue();
        }

        failFinalRead = false;
        clock.Advance();
        await Assert.That(await HandshakeAsync(rotated, server.PeerAddress!, hello)).IsTrue();
        await Assert.That(await HandshakeAsync(original, server.PeerAddress!, hello)).IsFalse();
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
    public async Task An_untrusted_peer_with_a_missing_issuer_cannot_trigger_certificate_downloads()
    {
        using var tls = new PeerTls();
        using var evidence = new TlsEvidence();
        using var issuer = new TcpListener(IPAddress.Loopback, 0);
        issuer.Start(1);
        var issuerUrl = $"http://127.0.0.1:{((IPEndPoint)issuer.LocalEndpoint).Port}/issuer.cer";
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityRequest = new CertificateRequest($"CN={EntityIds.New()}", issuerKey, HashAlgorithmName.SHA256);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=untrusted-peer", clientKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(PeerTlsFiles.ClientAuthentication)], false));
        request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [issuerUrl], false));
        using var leaf = request.Create(authority, authority.NotBefore, authority.NotAfter, RandomNumberGenerator.GetBytes(16));
        using var client = leaf.CopyWithPrivateKey(clientKey);
        var downloads = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var serving = ServeIssuerAsync();
        try
        {
            await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, evidence.Register);
            using var handler = tls.CreateHandler();
            handler.SslOptions.ClientCertificates = null;
            // Construct offline and omit the issuer, so only server-side chain validation can fetch it.
            handler.SslOptions.ClientCertificateContext = SslStreamCertificateContext.Create(client, null, offline: true);
            var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(Database(), RandomNumberGenerator.GetBytes(16)) };
            await Assert.That(await HandshakeAsync(tls, server.PeerAddress!, hello, evidence: evidence, handler: handler)).IsFalse();
            await Assert.That(await evidence.Authentication.WaitAsync(stop.Token) is AuthenticationException).IsTrue();
            await Assert.That(Volatile.Read(ref downloads)).IsEqualTo(0);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
        }

        async Task ServeIssuerAsync()
        {
            try
            {
                while (true)
                {
                    using var connection = await issuer.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref downloads);
                    var stream = connection.GetStream();
                    var body = authority.RawData;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    [Test]
    public async Task A_rotation_applies_only_once_the_certificate_chains_to_the_new_authority()
    {
        var clock = new PeerCredentialClock();
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services => services.AddSingleton<TimeProvider>(clock));
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) };

        original.OverwriteAuthorityWith(rotated);
        clock.Advance();
        var halfwayOriginal = await HandshakeAsync(original, server.PeerAddress!, hello);
        var halfwayRotated = await HandshakeAsync(rotated, server.PeerAddress!, hello);
        original.OverwriteCertificateWith(rotated);
        clock.Advance();
        var doneOriginal = await HandshakeAsync(original, server.PeerAddress!, hello);
        var doneRotated = await HandshakeAsync(rotated, server.PeerAddress!, hello);

        // With only the authority replaced, the old certificate does not chain to it: the node keeps the old pair.
        await Assert.That(halfwayOriginal).IsTrue();
        await Assert.That(halfwayRotated).IsFalse();
        await Assert.That(doneOriginal).IsFalse();
        await Assert.That(doneRotated).IsTrue();
    }

    [Test]
    public async Task Unreadable_certificate_files_keep_the_previous_certificates()
    {
        var clock = new PeerCredentialClock();
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services => services.AddSingleton<TimeProvider>(clock));
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(await server.RegisterAsync(), RandomNumberGenerator.GetBytes(16)) };

        var loaded = await HandshakeAsync(tls, server.PeerAddress!, hello);
        tls.DeleteNodeFiles();
        clock.Advance();
        var afterDelete = await HandshakeAsync(tls, server.PeerAddress!, hello);

        await Assert.That(loaded).IsTrue();
        await Assert.That(afterDelete).IsTrue();
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
    public async Task Peer_listener_rejects_clients_without_a_certificate()
    {
        using var tls = new PeerTls();
        using var evidence = new TlsEvidence();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, evidence.Register);
        var hello = new SyncRequest { Hello = ReplicationProtocol.CreateHello(Database(), RandomNumberGenerator.GetBytes(16)) };

        await Assert.That(await HandshakeAsync(tls, server.PeerAddress!, hello, evidence: evidence, presentClientCertificate: false)).IsFalse();
        await Assert.That(await evidence.Authentication.WaitAsync(TimeSpan.FromSeconds(30)) is AuthenticationException).IsTrue();
    }

    [Test]
    public async Task Listeners_stay_separate()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var peerHttp = new HttpClient(tls.CreateHandler())
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using var adminOnPeer = await peerHttp.GetAsync(new Uri(server.PeerAddress!, "/healthz"));
        using var grpcOnAdmin = await server.Client.PostAsync(
            new Uri("/terrabase.v1.Replication/Sync", UriKind.Relative), new ByteArrayContent([]));

        await Assert.That(adminOnPeer.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(grpcOnAdmin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_peer_destination_cannot_use_an_ephemeral_port()
    {
        using var tls = new PeerTls();
        await Assert.That(async () =>
        {
            await using var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["peer-addresses"] = "https://localhost:0";
            });
        }).Throws<InvalidOperationException>();

        await using var defaultPort = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["peer-addresses"] = "https://localhost";
        });
        await Assert.That(defaultPort.Services.GetRequiredService<TerraBaseOptions>().PeerAddresses.Single().Port).IsEqualTo(443);
        using var health = await defaultPort.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        health.EnsureSuccessStatusCode();
    }

    [Test]
    [Arguments("peer-addresses", "https://localhost:7000", "peer-port")]
    [Arguments("peer-addresses", "http://localhost:7000", "https://host:port")]
    [Arguments("peer-addresses", "https://localhost:7000/path", "https://host:port")]
    [Arguments("peer-addresses", "https://a:1,https://a:1", "distinct")]
    [Arguments("catalog-primary", "https://localhost:7000", "peer-port")]
    [Arguments("catalog-primary", "http://localhost:7000", "https://host:port")]
    [Arguments("catalog-primary", "https://localhost:7000,https://other:7000", "https://host:port")]
    public async Task Peer_addresses_must_be_distinct_https_origins(string key, string value, string message)
    {
        var settings = new Dictionary<string, string> { [key] = value };

        var error = await Assert.That(() => TerraBaseOptions.FromValues(settings)).Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains(message);
        await Assert.That(error.Message).DoesNotContain("7000");
    }

    [Test]
    public async Task Peers_name_every_member_and_derive_the_peer_addresses_and_catalog_primary()
    {
        using var tls = new PeerTls();
        Dictionary<string, string> Settings(string node, string primary)
        {
            var settings = new Dictionary<string, string>
            {
                ["node-name"] = node,
                ["peers"] = "ts-gru-1=10.251.0.21:9443, ts-gru-2=10.251.0.22:9443",
                ["catalog-primary"] = primary,
            };
            tls.Apply(settings);
            return settings;
        }

        using (var primary = TerraBaseOptions.FromValues(Settings("ts-gru-1", "ts-gru-1")))
        {
            await Assert.That(primary.PeerAddresses.Select(address => address.ToString())).IsEquivalentTo(["https://10.251.0.22:9443/"]);
            await Assert.That(primary.CatalogPrimary).IsNull();
        }

        using var follower = TerraBaseOptions.FromValues(Settings("ts-gru-2", "ts-gru-1"));
        await Assert.That(follower.PeerAddresses.Select(address => address.ToString())).IsEquivalentTo(["https://10.251.0.21:9443/"]);
        await Assert.That(follower.CatalogPrimary!.ToString()).IsEqualTo("https://10.251.0.21:9443/");

        // peers needs the peer listener, and replaces peer-addresses rather than adding to it.
        var withoutListener = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["peers"] = "a=localhost:7000", ["node-name"] = "a" }))
            .Throws<TerraBaseConfigurationException>();
        await Assert.That(withoutListener!.Message).Contains("peer-port");
        var both = Settings("ts-gru-1", "ts-gru-1");
        both["peer-addresses"] = "https://localhost:7001";
        var twice = await Assert.That(() => TerraBaseOptions.FromValues(both)).Throws<TerraBaseConfigurationException>();
        await Assert.That(twice!.Message).Contains("not both");
    }

    [Test]
    [Arguments("a=localhost:7000", "b", "", "node-name")]
    [Arguments("a=localhost:7000,a=localhost:7001", "a", "", "distinct")]
    [Arguments("a=localhost:7000,b=localhost:7000", "a", "", "distinct")]
    [Arguments("A=localhost:7000", "A", "", "name=host:port")]
    [Arguments("a=localhost:7000/path", "a", "", "name=host:port")]
    [Arguments("a=localhost:7000", "a", "c", "catalog-primary")]
    public async Task Peers_must_be_distinct_members_naming_this_node(string peers, string node, string primary, string message)
    {
        using var tls = new PeerTls();
        var settings = new Dictionary<string, string> { ["peers"] = peers, ["node-name"] = node };
        tls.Apply(settings);
        if (primary.Length > 0)
        {
            settings["catalog-primary"] = primary;
        }

        var error = await Assert.That(() => TerraBaseOptions.FromValues(settings)).Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains(message);
        await Assert.That(error.Message).DoesNotContain("7000");
    }

    [Test]
    [Arguments("peer-port")]
    [Arguments("peer-client-ca")]
    public async Task Peer_settings_must_be_complete(string only)
    {
        var error = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { [only] = "0" }))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("peer-port");
    }

    [Test]
    public async Task Unreadable_peer_certificates_are_rejected_without_echoing_paths()
    {
        var settings = new Dictionary<string, string>
        {
            ["peer-port"] = "0",
            ["peer-certificate"] = "/nonexistent/secret-server.pem",
            ["peer-private-key"] = "/nonexistent/secret-server.key",
            ["peer-client-ca"] = "/nonexistent/secret-ca.pem",
        };

        var error = await Assert.That(() => TerraBaseOptions.FromValues(settings)).Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("peer-certificate");
        await Assert.That(error.Message).DoesNotContain("secret");
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
    private static async Task<bool> HandshakeAsync(
        PeerTls tls, Uri address, SyncRequest hello, X509Certificate2? clientCertificate = null,
        TlsEvidence? evidence = null, bool presentClientCertificate = true, SocketsHttpHandler? handler = null)
    {
        var authentication = evidence?.ObserveNextHandshake();
        var transport = handler ?? tls.CreateHandler(presentClientCertificate, clientCertificate);
        // A fresh connection must exercise certificate authentication rather than resume a TLS session.
        transport.SslOptions.AllowTlsResume = false;
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = transport, DisposeHttpClient = handler is null });
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        try
        {
            using var call = client.Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await call.RequestStream.WriteAsync(hello);
            if (!await call.ResponseStream.MoveNext(CancellationToken.None) || call.ResponseStream.Current.Welcome is null)
            {
                throw new InvalidDataException("TLS succeeded but the peer did not answer with Welcome");
            }

            if (authentication is not null && await authentication.WaitAsync(TimeSpan.FromSeconds(30)) is not null)
            {
                throw new InvalidDataException("Welcome arrived despite a reported TLS authentication failure");
            }

            return true;
        }
        catch (Exception error) when (IsTlsRejection(error)
            || (authentication is not null && (error is IOException or HttpRequestException
                || error is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.Internal })))
        {
            var rejected = authentication is not null
                && await authentication.WaitAsync(TimeSpan.FromSeconds(30)) is AuthenticationException;
            if (IsTlsRejection(error, rejected))
            {
                return false;
            }

            throw;
        }
    }

    // A failed TLS handshake as the gRPC client reports it: the client refusing the server certificate
    // (AuthenticationException), or transport I/O failure accompanied by Kestrel's authentication failure for this probe.
    // A connection aborted after successful TLS also gives InvalidResponse, so it alone proves nothing.
    private static bool IsTlsRejection(Exception error, bool authenticationRejected = false)
    {
        if (error is RpcException { StatusCode: not (StatusCode.Unavailable or StatusCode.Internal) })
        {
            return false;
        }

        return Causes(error).Any(cause => cause is AuthenticationException
            || (authenticationRejected && cause is IOException));
    }

    private static IEnumerable<Exception> Causes(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException ?? (cause as RpcException)?.Status.DebugException)
        {
            yield return cause;
        }
    }

    private static string Fingerprint(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    // One observer per isolated server; probes open fresh connections and run one at a time within the test.
    private sealed class TlsEvidence : ILoggerProvider, ILogger
    {
        private volatile TaskCompletionSource<Exception?> _authentication = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Exception?> Authentication => _authentication.Task;

        public Task<Exception?> ObserveNextHandshake()
        {
            _authentication = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Authentication;
        }

        public void Register(IServiceCollection services) =>
            services.AddLogging(logging => logging.AddProvider(this).AddFilter<TlsEvidence>(level => level == LogLevel.Debug));

        public ILogger CreateLogger(string categoryName) =>
            categoryName == "Microsoft.AspNetCore.Server.Kestrel.Https.Internal.HttpsConnectionMiddleware" ? this : NullLogger.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "AuthenticationFailed" && exception is not null)
            {
                _authentication.TrySetResult(exception);
            }
            else if (eventId.Name == "HttpsConnectionEstablished")
            {
                _authentication.TrySetResult(null);
            }
        }

        public void Dispose()
        {
        }
    }

    private static TenantContext Database() =>
        new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
}
