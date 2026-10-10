using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bogus;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

public sealed class ReplicationSlowTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_locked_source_journal_expires_its_attempt_and_allows_a_healthy_database_to_replicate(bool beforeDiscovery)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-journal-deadline-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var blockedWelcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var discoveryTimeout = new DiscoveryTimeoutEvidence();
        TerraBaseTestServer? sender = null;
        FakePeer? peer = null;
        SqliteConnection? locked = null;
        var lockedOnce = 0;
        try
        {
            ReplicationTenant tenant;
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "broken", "healthy");
                await Assert.That(await seed.Services.GetRequiredService<IManagementService>().SetPlanAsync(
                    tenant.UserId, tenant.OrganizationId, PlanTier.Enterprise, deadline.Token) is Organization).IsTrue();
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                foreach (var name in new[] { "broken", "healthy" })
                {
                    using var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path(name)}/collections", UriKind.Relative), new { name = "items" }, deadline.Token);
                    collection.EnsureSuccessStatusCode();
                }

                using var put = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("broken")}/collections/items/documents/by-key?id={ReplicationDocuments.Id("blocked")}", UriKind.Relative), new { value = "blocked" }, deadline.Token);
                put.EnsureSuccessStatusCode();
            }

            // WAL readers ignore writer locks; change mode while no live host can repool a connection.
            var file = Path.Combine(data, tenant.OrganizationId, tenant.ProjectId, tenant.Database("broken").DatabaseId + ".db");
            locked = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
            await locked.OpenAsync(deadline.Token);
            using (var mode = locked.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode = DELETE;";
                await Assert.That((string)(await mode.ExecuteScalarAsync(deadline.Token))!).IsEqualTo("delete");
            }

            if (beforeDiscovery)
            {
                // Hold the native lock before the sender exists, covering its first open/migration
                // as well as discovery's later head read. No in-memory store has been published yet.
                using var hold = locked.CreateCommand();
                hold.CommandText = "BEGIN EXCLUSIVE;";
                await hold.ExecuteNonQueryAsync(deadline.Token);
            }

            var brokenId = ReplicationProtocol.CreateHello(tenant.Database("broken"), new byte[16]).LocalIdentity.DatabaseId;
            peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await requests.MoveNext(context.CancellationToken);
                var hello = requests.Current.Hello;
                var broken = hello.LocalIdentity.DatabaseId == brokenId;
                if (broken && !beforeDiscovery && Interlocked.Exchange(ref lockedOnce, 1) == 0)
                {
                    // Hello follows the ended fixed-head snapshot; only now block its source-proof read.
                    using var hold = locked.CreateCommand();
                    hold.CommandText = "BEGIN EXCLUSIVE;";
                    await hold.ExecuteNonQueryAsync(deadline.Token);
                    lockReady.TrySetResult();
                }

                if (broken && !beforeDiscovery)
                {
                    // A retry may arrive while the first handler is still preparing the native lock.
                    await lockReady.Task.WaitAsync(context.CancellationToken);
                }

                await responses.WriteAsync(FakePeer.Welcome(hello, out var member), context.CancellationToken);
                if (broken)
                {
                    // The source proof must now wait behind the still-held EXCLUSIVE lock.
                    blockedWelcome.TrySetResult();
                }

                while (await requests.MoveNext(context.CancellationToken))
                {
                    var batch = requests.Current.CommittedGroup;
                    await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
                    // Group 1 created the healthy collection before the lock; group 2 is the write made under it.
                    if (!broken && batch.EventId.Sequence == 2)
                    {
                        healthyReceived.TrySetResult();
                    }
                }
            });
            sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = peer.Address.ToString();
            }, services => services.AddSingleton<ILoggerProvider>(discoveryTimeout));
            if (!beforeDiscovery)
            {
                await blockedWelcome.Task.WaitAsync(deadline.Token);
            }
            using var client = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
            using var added = await client.PutAsJsonAsync(new Uri($"{tenant.Path("healthy")}/collections/items/documents/by-key?id={ReplicationDocuments.Id("healthy")}", UriKind.Relative), new { value = "healthy" }, deadline.Token);
            added.EnsureSuccessStatusCode();
            // Shorter than SQLite's 30 s lock timeout, longer than the real 15 s progress budget.
            // The exclusive lock stays held while healthy work advances. Startup also requires
            // positive evidence that discovery cancelled native work instead of waiting all 30 s.
            await healthyReceived.Task.WaitAsync(TimeSpan.FromSeconds(25), deadline.Token);
            if (beforeDiscovery)
            {
                await discoveryTimeout.Fired.Task.WaitAsync(TimeSpan.FromSeconds(25), deadline.Token);
                await Assert.That(await sender.MetricAsync("terrabase_replication_sender_initialized", "", deadline.Token)).IsEqualTo(0);
            }
            else
            {
                // The broken database's collection creation and write.
                await TerraBaseTestServer.EventuallyAsync(async token =>
                    await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == 2);
            }

            await Assert.That(locked).IsNotNull();
            await locked!.DisposeAsync();
            locked = null;
            // Discovery retries the cancelled database and publishes a complete initialization result.
            await TerraBaseTestServer.ReplicatedAsync(sender);
        }
        finally
        {
            if (locked is not null)
            {
                await locked.DisposeAsync();
            }

            if (sender is not null)
            {
                await sender.DisposeAsync();
            }

            if (peer is not null)
            {
                await peer.DisposeAsync();
            }

            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task The_receiver_ends_a_stream_that_stays_idle()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "idle");
        using var channel = tls.CreateChannel(server.PeerAddress!);
        // A client deadline well beyond the receiver's idle timeout bounds the test if the receiver never ends the stream.
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(deadline: DateTime.UtcNow.AddSeconds(90));
        var database = tenant.Database("idle");
        // The receiver's idle wait begins before Welcome reaches the client.
        var clock = Stopwatch.StartNew();
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16)) });
        await call.ResponseStream.MoveNext(CancellationToken.None);
        var welcome = call.ResponseStream.Current.Welcome;

        var error = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None)).Throws<RpcException>();

        await Assert.That(welcome).IsNotNull();
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.DeadlineExceeded);
        await Assert.That(error.Status.Detail).IsEqualTo("the peer made no progress on the Sync stream within the idle timeout");
        await Assert.That(clock.Elapsed).IsGreaterThanOrEqualTo(ReplicationProtocol.IdleTimeout - TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Two_nodes_converge_after_many_concurrent_writes_and_deletes()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, port, peerPort) => Configure(settings, peerTls, clientTls, port, peerPort));
        await using var disposeA = a;
        await using var disposeB = b;
        var tenant = await ReplicationTenant.CreateAsync(a, "race");
        await tenant.BindAsync(b);
        foreach (var node in new[] { a, b })
        {
            await Assert.That(await node.Services.GetRequiredService<IManagementService>().SetPlanAsync(
                tenant.UserId, tenant.OrganizationId, PlanTier.Enterprise, CancellationToken.None) is Organization).IsTrue();
        }
        using var clientA = clientTls.CreateClient(a.ClientAddress!, await tenant.KeyAsync(a, "documents:read", "documents:write"));
        using var clientB = clientTls.CreateClient(b.ClientAddress!, await tenant.KeyAsync(b, "documents:read", "documents:write"));
        var path = tenant.Path("race");
        // Created once: it reaches b, and creating it on b as well would conflict.
        await CreateCollectionAsync(clientA, path);
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        await PutAsync(clientA, path, "deleted", new Faker());

        // Four writers per node race over the same 20 documents: about one in four operations is a delete.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => WriteAsync(writer < 4 ? clientA : clientB, path, new Faker { Random = new Randomizer(writer) }))
            .Append(DeleteAsync(clientA, path, "deleted")));
        // Barrier: every group either node committed is durably applied on the other, so nothing still in
        // flight can change either of them.
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        await Assert.That(await ListAsync(clientB, path)).IsEquivalentTo(await ListAsync(clientA, path));
        var versions = await VersionsAsync(a, tenant.Database("race"));
        await Assert.That(await VersionsAsync(b, tenant.Database("race"))).IsEquivalentTo(versions);
        var (_, tombstone) = versions.Single(document => document.Id == "deleted");
        await Assert.That(tombstone).IsNotNull();
        await Assert.That(tombstone!.Value.Deleted).IsTrue();
        await Assert.That(tombstone.Value.Version).IsGreaterThan(1);
        foreach (var client in new[] { clientA, clientB })
        {
            using var deleted = await client.GetAsync(new Uri($"{path}/collections/items/documents/by-key?id={ReplicationDocuments.Id("deleted")}", UriKind.Relative));
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // A final write per raced document then supersedes everything both nodes have seen: all 20 exist.
        foreach (var id in Enumerable.Range(0, 20))
        {
            await PutAsync(id % 2 == 0 ? clientA : clientB, path, $"doc-{id}", new Faker());
        }

        await TerraBaseTestServer.ReplicatedAsync(a, b);
        var left = await ListAsync(clientA, path);
        var right = await ListAsync(clientB, path);

        await Assert.That(left.Count).IsEqualTo(20);
        await Assert.That(right).IsEquivalentTo(left);
    }

    [Test]
    public async Task Advancing_ACKs_keep_a_bandwidth_limited_window_alive_while_groups_are_written()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-slow-window-").FullName;
        var streams = 0;
        var delivered = 0;
        var applied = 0UL;
        var finished = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                var tenant = await ReplicationTenant.CreateAsync(seed, "paced");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                await CreateCollectionAsync(writer, tenant.Path("paced"));
                var faker = new Faker();
                foreach (var index in Enumerable.Range(0, 8))
                {
                    using var response = await writer.PutAsJsonAsync(
                        new Uri($"{tenant.Path("paced")}/collections/items/documents/by-key?id={ReplicationDocuments.Id($"b{index}")}", UriKind.Relative),
                        new { data = Convert.ToBase64String(faker.Random.Bytes(600 * 1024)) });
                    response.EnsureSuccessStatusCode();
                }

                // The peer already holds group 1, the collection's creation, so only the eight large groups travel.
                var store = (await seed.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("paced"), CancellationToken.None))!;
                await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
                applied = 1;
                await Assert.That(await read.GetLastChangeSequenceAsync(CancellationToken.None)).IsEqualTo(9);
            }

            await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await requests.MoveNext(context.CancellationToken);
                var hello = requests.Current.Hello;
                var welcome = FakePeer.Welcome(hello, out var member);
                welcome.Welcome.Frontiers.Add(new PeerFrontier
                {
                    Origin = hello.LocalIdentity is { } identity
                        ? new OriginIdentity { ReplicaId = identity.MemberIncarnation, Incarnation = identity.MemberIncarnation }
                        : throw new InvalidDataException("Hello has no origin"),
                    DurableSequence = applied,
                });
                await responses.WriteAsync(welcome, context.CancellationToken);
                var elapsed = Stopwatch.StartNew();
                var transferring = false;
                // Pace real HTTP/2 request consumption. Each group is acknowledged immediately once read,
                // while the aggregate window takes longer than the sender's progress timeout to transfer.
                using var bandwidth = new PeriodicTimer(TimeSpan.FromSeconds(3));
                while (await bandwidth.WaitForNextTickAsync(context.CancellationToken)
                    && await requests.MoveNext(context.CancellationToken))
                {
                    var batch = requests.Current.CommittedGroup;
                    if (!transferring)
                    {
                        Interlocked.Increment(ref streams);
                        transferring = true;
                    }
                    await Assert.That(batch.EncodedApplicationBytes).IsGreaterThan(500U * 1024);
                    applied = batch.EventId.Sequence;
                    await responses.WriteAsync(FakePeer.Ack(member, batch, applied), context.CancellationToken);
                    if (Interlocked.Increment(ref delivered) == 8)
                    {
                        finished.TrySetResult(elapsed.Elapsed);
                    }
                }
            });
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = peer.Address.ToString();
            });
            await Assert.That(await finished.Task.WaitAsync(TimeSpan.FromSeconds(60))).IsGreaterThan(ReplicationProtocol.ProgressTimeout);
            await TerraBaseTestServer.ReplicatedAsync(sender);
            await Assert.That(Volatile.Read(ref delivered)).IsEqualTo(8);
            await Assert.That(Volatile.Read(ref streams)).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task A_stream_whose_acknowledgments_never_advance_is_abandoned_and_retried()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-slow-stale-").FullName;
        var clock = new ProgressClock();
        var streams = new System.Collections.Concurrent.ConcurrentQueue<HeldStream>();
        try
        {
            var database = await SeedAsync(clientTls, data);
            await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await requests.MoveNext(context.CancellationToken);
                var hello = requests.Current.Hello;
                if (hello.LocalIdentity.DatabaseId != database)
                {
                    await AcknowledgeAsync(hello, requests, responses, context);
                    return;
                }

                var stream = new HeldStream(clock.ActiveDeadline);
                streams.Enqueue(stream);
                // One group in flight, so only an acknowledgment that releases the first group lets the second travel.
                var limits = hello.Limits.Clone();
                limits.MaxInFlightBatches = 1;
                try
                {
                    await responses.WriteAsync(FakePeer.Welcome(hello, out var member, limits), context.CancellationToken);
                    var group = await NextGroupAsync(requests, context.CancellationToken);
                    stream.Group.TrySetResult(group);
                    await stream.Release.Task.WaitAsync(context.CancellationToken);
                    // A valid but stale acknowledgment, then an advancing one behind it on the same stream: the
                    // second group proves the sender processed both, in order.
                    await responses.WriteAsync(FakePeer.Ack(member, group, 0), context.CancellationToken);
                    await responses.WriteAsync(FakePeer.Ack(member, group, group.EventId.Sequence), context.CancellationToken);
                    stream.Next.TrySetResult(await NextGroupAsync(requests, context.CancellationToken));
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or InvalidDataException)
                {
                    // The sender gave up on this stream.
                    stream.Group.TrySetException(error);
                    stream.Next.TrySetException(error);
                    stream.Abandoned.TrySetResult();
                }
            });
            await using var sender = await StartSenderAsync(peerTls, data, peer, clock);

            // Time stands still until the first group arrives, so every deadline below is exact.
            var stream = await FirstAsync(streams);
            await stream.Group.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromSeconds(10));
            var armed = stream.Deadline.Rearms.Count;
            stream.Release.TrySetResult();
            await stream.Next.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Only the advancing acknowledgment re-armed the deadline; the stale one before it did not.
            await Assert.That(stream.Deadline.Rearms.Count - armed).IsEqualTo(1);
            var extended = clock.GetUtcNow() + ReplicationProtocol.ProgressTimeout;

            clock.Advance(ReplicationProtocol.ProgressTimeout - TimeSpan.FromTicks(1));
            await Assert.That(stream.Deadline.Expired.Task.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromTicks(1));
            await Assert.That(await stream.Deadline.Expired.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(extended);
            await stream.Abandoned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await RetriedAsync(clock, streams);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task A_peer_that_never_answers_Hello_is_abandoned_and_retried()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-slow-hello-").FullName;
        var clock = new ProgressClock();
        var streams = new System.Collections.Concurrent.ConcurrentQueue<HeldStream>();
        try
        {
            var database = await SeedAsync(clientTls, data);
            await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await requests.MoveNext(context.CancellationToken);
                var hello = requests.Current.Hello;
                if (hello.LocalIdentity.DatabaseId != database)
                {
                    await AcknowledgeAsync(hello, requests, responses, context);
                    return;
                }

                var stream = new HeldStream(clock.ActiveDeadline);
                streams.Enqueue(stream);
                try
                {
                    // Reads Hello and then never answers.
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The sender gave up on this stream.
                    stream.Abandoned.TrySetResult();
                }
            });
            await using var sender = await StartSenderAsync(peerTls, data, peer, clock);

            // The attempt's deadline was armed before its snapshot and Hello, at the frozen start time.
            var stream = await FirstAsync(streams);
            clock.Advance(ReplicationProtocol.ProgressTimeout);
            await Assert.That(await stream.Deadline.Expired.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(clock.Start + ReplicationProtocol.ProgressTimeout);
            await stream.Abandoned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await RetriedAsync(clock, streams);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task A_Welcome_one_tick_before_the_progress_deadline_keeps_the_stream()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-slow-welcome-").FullName;
        var clock = new ProgressClock();
        var streams = new System.Collections.Concurrent.ConcurrentQueue<HeldStream>();
        var scripted = 0;
        var releaseAcknowledgment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var database = await SeedAsync(clientTls, data);
            await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await requests.MoveNext(context.CancellationToken);
                var hello = requests.Current.Hello;
                // Only the first stream is held; a later verification stream must not wait for a release.
                if (hello.LocalIdentity.DatabaseId != database || Interlocked.Exchange(ref scripted, 1) == 1)
                {
                    await AcknowledgeAsync(hello, requests, responses, context);
                    return;
                }

                var stream = new HeldStream(clock.ActiveDeadline);
                streams.Enqueue(stream);
                PeerIdentity member;
                try
                {
                    await stream.Release.Task.WaitAsync(context.CancellationToken);
                    await responses.WriteAsync(FakePeer.Welcome(hello, out member), context.CancellationToken);
                    var group = await NextGroupAsync(requests, context.CancellationToken);
                    stream.Group.TrySetResult(group);
                    await releaseAcknowledgment.Task.WaitAsync(context.CancellationToken);
                    await responses.WriteAsync(FakePeer.Ack(member, group, group.EventId.Sequence), context.CancellationToken);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or InvalidDataException)
                {
                    // The sender gave up on this stream before its first group.
                    stream.Group.TrySetException(error);
                    return;
                }

                while (await requests.MoveNext(context.CancellationToken))
                {
                    var batch = requests.Current.CommittedGroup;
                    await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
                }
            });
            await using var sender = await StartSenderAsync(peerTls, data, peer, clock);

            var stream = await FirstAsync(streams);
            clock.Advance(ReplicationProtocol.ProgressTimeout - TimeSpan.FromTicks(1));
            await Assert.That(stream.Deadline.Expired.Task.IsCompleted).IsFalse();
            stream.Release.TrySetResult();
            var group = await stream.Group.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(group.EventId.Sequence).IsEqualTo(1UL);
            // The first group proves Welcome was processed; no ACK may extend the deadline yet.
            clock.Advance(TimeSpan.FromTicks(1));
            await Assert.That(stream.Deadline.Expired.Task.IsCompleted).IsFalse();
            releaseAcknowledgment.TrySetResult();
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_database_written_without_pause_does_not_hold_back_the_others(bool rejectQuietWrite)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        using var receiving = peerTls.CreateChannel(receiver.PeerAddress!);
        var rpc = new Features.Replication.V1.Replication.ReplicationClient(receiving);
        var firstBusy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quietDurable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyDurable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var furtherBusy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFurther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fairnessFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Busy relay progress, reported only when quiet replication fails.
        var progress = new Lock();
        var clock = Stopwatch.StartNew();
        ulong busyForwarded = 0, busyAcked = 0;
        long busyForwardedAt = -1, busyAckedAt = -1, releasedAt = -1;
        Google.Protobuf.ByteString? busyId = null, quietId = null;
        await using var gate = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var token = relayStop.Token;
            if (!await requests.MoveNext(token))
            {
                return;
            }

            var database = requests.Current.Hello.LocalIdentity.DatabaseId;
            using var call = rpc.Sync(cancellationToken: token);
            var reading = ForwardResponsesAsync();
            try
            {
                do
                {
                    if (database == busyId && requests.Current.CommittedGroup is { } batch)
                    {
                        // Group 1 created the collection; group 2 is the initial write.
                        if (batch.EventId.Sequence == 2)
                        {
                            firstBusy.TrySetResult();
                            await releaseFirst.Task.WaitAsync(token);
                        }
                        else if (batch.EventId.Sequence == 259)
                        {
                            // Initial head is two, followed by at most 256 groups in the next busy turn.
                            // A sender draining continuously reaches this gate before serving quiet.
                            if (!quietDurable.Task.IsCompletedSuccessfully)
                            {
                                fairnessFailure.TrySetException(new InvalidOperationException("busy exceeded its next turn before quiet committed"));
                            }

                            furtherBusy.TrySetResult();
                            await releaseFurther.Task.WaitAsync(token);
                        }
                    }

                    await call.RequestStream.WriteAsync(requests.Current, token);
                    if (database == busyId && requests.Current.CommittedGroup is { } forwarded)
                    {
                        lock (progress)
                        {
                            if (forwarded.EventId.Sequence > busyForwarded)
                            {
                                busyForwarded = forwarded.EventId.Sequence;
                                busyForwardedAt = clock.ElapsedMilliseconds;
                            }
                        }
                    }
                }
                while (await requests.MoveNext(token));
                await call.RequestStream.CompleteAsync();
                await reading;
            }
            finally
            {
                await relayStop.CancelAsync();
                call.Dispose();
                try
                {
                    await reading;
                }
                catch (Exception error) when (relayStop.IsCancellationRequested && error is RpcException or OperationCanceledException)
                {
                    // The relay owns and joins its one response reader on every exit.
                }
            }

            async Task ForwardResponsesAsync()
            {
                while (await call.ResponseStream.MoveNext(token))
                {
                    var response = call.ResponseStream.Current;
                    // Quiet's group 1 created its collection; group 2 is its write.
                    if (database == quietId && response.Durable is { DurablePrefix: >= 2 })
                    {
                        quietDurable.TrySetResult();
                    }

                    if (database == busyId && response.Durable is { DurablePrefix: >= 258 })
                    {
                        busyDurable.TrySetResult();
                    }

                    if (database == busyId && response.Durable is { } acked)
                    {
                        lock (progress)
                        {
                            if (acked.DurablePrefix > busyAcked)
                            {
                                busyAcked = acked.DurablePrefix;
                                busyAckedAt = clock.ElapsedMilliseconds;
                            }
                        }
                    }

                    await responses.WriteAsync(response, token);
                }
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = gate.Address.ToString();
            // This measures the sender's turns under writers that never pause; write-wait would hold every busy write
            // on the group the gate holds.
            settings["write-wait-ms"] = "0";
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "busy", "quiet");
        busyId = Google.Protobuf.ByteString.CopyFrom(Ulid.Parse(tenant.Database("busy").DatabaseId!).ToByteArray());
        quietId = Google.Protobuf.ByteString.CopyFrom(Ulid.Parse(tenant.Database("quiet").DatabaseId!).ToByteArray());
        await tenant.BindAsync(receiver);
        foreach (var node in new[] { sender, receiver })
        {
            await Assert.That(await node.Services.GetRequiredService<IManagementService>().SetPlanAsync(
                tenant.UserId, tenant.OrganizationId, PlanTier.Enterprise, CancellationToken.None) is Organization).IsTrue();
        }
        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        var key = await tenant.KeyAsync(sender, "documents:write");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, key);
        using var quietWriter = clientTls.CreateClient(sender.ClientAddress!, rejectQuietWrite ? null : key);
        foreach (var database in new[] { "busy", "quiet" })
        {
            await CreateCollectionAsync(writer, tenant.Path(database));
        }

        // Hold an actual busy group while accumulating backlog, then keep four writers committing.
        using var stop = new CancellationTokenSource();
        Task? busy = null;
        HttpStatusCode? failedStatus = null;
        try
        {
            await PutAsync(writer, tenant.Path("busy"), "initial", new Faker());
            await firstBusy.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("busy"), stop.Token))!;
            await using (var read = await source.BeginTransactionAsync(write: false, stop.Token))
            {
                await Assert.That(await read.GetLastChangeSequenceAsync(stop.Token)).IsEqualTo(2);
            }

            busy = Task.WhenAll(Enumerable.Range(0, 4).Select(writerId => Task.Run(async () =>
            {
                var faker = new Faker { Random = new Randomizer(writerId) };
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var response = await writer.PutAsJsonAsync(
                            new Uri($"{tenant.Path("busy")}/collections/items/documents/by-key?id={ReplicationDocuments.Id($"w{writerId}-{faker.Random.Int(0, 50)}")}", UriKind.Relative),
                            new { note = faker.Lorem.Sentence() }, stop.Token);
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Cleanup cancels an active PUT before joining the writers.
                }
            })));
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                await using var read = await source.BeginTransactionAsync(write: false, token);
                return await read.GetLastChangeSequenceAsync(token) >= 512;
            });
            using var quietResponse = await quietWriter.PutAsJsonAsync(new Uri($"{tenant.Path("quiet")}/collections/items/documents/by-key?id={ReplicationDocuments.Id("q1")}", UriKind.Relative), new { note = "quiet" });
            quietResponse.EnsureSuccessStatusCode();
            releasedAt = clock.ElapsedMilliseconds;
            releaseFirst.TrySetResult();
            var quietReads = 0;
            HttpStatusCode? quietStatus = null;
            try
            {
                await TerraBaseTestServer.EventuallyAsync(async token =>
                {
                    if (fairnessFailure.Task.IsFaulted)
                    {
                        await fairnessFailure.Task;
                    }

                    quietReads++;
                    using var response = await reader.GetAsync(new Uri($"{tenant.Path("quiet")}/collections/items/documents/by-key?id={ReplicationDocuments.Id("q1")}", UriKind.Relative), token);
                    quietStatus = response.StatusCode;
                    return response.IsSuccessStatusCode;
                }, TimeSpan.FromSeconds(30));
            }
            catch (Exception error)
            {
                var now = clock.ElapsedMilliseconds;
                string busyProgress;
                lock (progress)
                {
                    busyProgress = $"busyForwarded={busyForwarded}@{busyForwardedAt}ms, busyAcked={busyAcked}@{busyAckedAt}ms";
                }

                throw new InvalidOperationException(
                    $"Quiet replication did not finish: reads={quietReads}, status={quietStatus}, durable={quietDurable.Task.Status}, busyGate={furtherBusy.Task.Status}, fairness={fairnessFailure.Task.Status}, " +
                    $"{busyProgress}, releasedAt={releasedAt}ms, sinceRelease={now - releasedAt}ms, now={now}ms", error);
            }

            // Holding request 259 proves 258 was forwarded; its durable response proves it committed.
            await Task.WhenAll(furtherBusy.Task, busyDurable.Task).WaitAsync(TimeSpan.FromSeconds(30));
            if (fairnessFailure.Task.IsFaulted)
            {
                await fairnessFailure.Task;
            }

            var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("busy"), stop.Token))!;
            await using (var local = await source.BeginTransactionAsync(write: false, stop.Token))
            await using (var remote = await target.BeginTransactionAsync(write: false, stop.Token))
            {
                var applied = await remote.GetAppliedSequenceAsync(await local.GetOriginAsync(stop.Token), stop.Token);
                await Assert.That(applied).IsEqualTo(258);
                await Assert.That(await local.GetLastChangeSequenceAsync(stop.Token)).IsGreaterThan(applied);
            }

            await Assert.That(busy.IsCompleted).IsFalse();
        }
        catch (HttpRequestException error) when (rejectQuietWrite)
        {
            failedStatus = error.StatusCode;
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseFurther.TrySetResult();
            await stop.CancelAsync();
            if (busy is not null)
            {
                await busy;
            }
        }

        await Assert.That(busy is { IsCompletedSuccessfully: true }).IsTrue();
        if (rejectQuietWrite)
        {
            await Assert.That(failedStatus).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task The_receiver_ends_a_stream_whose_peer_stops_reading_responses()
    {
        using var tls = new PeerTls();
        using var timeout = new ReceiverTimeoutEvidence();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply,
            services => services.AddLogging(logging => logging.AddProvider(timeout)));
        var tenant = await ReplicationTenant.CreateAsync(server, "deaf");
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(deadline: DateTime.UtcNow.AddSeconds(120));
        var database = tenant.Database("deaf");
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16)) });

        // Every KeepAlive is echoed; never reading the echoes fills the response window, so the receiver's
        // writes block. It must end the stream after the idle timeout rather than hold its slot forever.
        var clock = Stopwatch.StartNew();
        var flood = Task.Run(async () =>
        {
            for (ulong nonce = 0; ; nonce++)
            {
                await call.RequestStream.WriteAsync(new SyncRequest { KeepAlive = new KeepAlive { Nonce = nonce } });
            }
        });

        try
        {
            // A blocked response write ends at the receiver's idle timeout. Depending on whether Kestrel
            // aborts the stream or connection, the transport reports CANCEL or INTERNAL_ERROR. Neither
            // reset alone proves the timeout: require the server's specific DeadlineExceeded evidence too.
            var reset = await Assert.That(async () => await flood.WaitAsync(TimeSpan.FromSeconds(90))).Throws<HttpProtocolException>();
            var elapsed = clock.Elapsed;
            var status = await Assert.That(async () =>
            {
                while (await call.ResponseStream.MoveNext(CancellationToken.None))
                {
                }
            }).Throws<RpcException>();

            var expectedStatus = reset!.ErrorCode switch
            {
                0x8 => StatusCode.Cancelled,
                0x2 => StatusCode.Internal,
                _ => throw new InvalidDataException($"unexpected HTTP/2 reset {reset.ErrorCode}"),
            };
            await Assert.That(status!.StatusCode).IsEqualTo(expectedStatus);
            await timeout.Fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(elapsed).IsGreaterThanOrEqualTo(ReplicationProtocol.IdleTimeout - TimeSpan.FromSeconds(1));
        }
        finally
        {
            call.Dispose();
            try
            {
                await flood;
            }
            catch (Exception error) when (error is RpcException or HttpProtocolException or OperationCanceledException or ObjectDisposedException)
            {
                // Observe the writer's failure after aborting any pending write, before listener teardown.
            }
        }
    }

    private sealed class DiscoveryTimeoutEvidence : ILoggerProvider, ILogger
    {
        public TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) =>
            categoryName.EndsWith(".ReplicationSender", StringComparison.Ordinal) ? this : NullLogger.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is OperationCanceledException
                && formatter(state, exception).StartsWith("Replication discovery failed", StringComparison.Ordinal))
            {
                Fired.TrySetResult();
            }
        }

        public void Dispose()
        {
        }
    }

    // One isolated listener and stream: transport resets only count when its receiver reported the timeout.
    private sealed class ReceiverTimeoutEvidence : ILoggerProvider, ILogger
    {
        public TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) =>
            categoryName == "Grpc.AspNetCore.Server.ServerCallHandler" ? this : NullLogger.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("'DeadlineExceeded'", StringComparison.Ordinal)
                && message.Contains("the peer made no progress on the Sync stream within the idle timeout", StringComparison.Ordinal))
            {
                Fired.TrySetResult();
            }
        }

        public void Dispose()
        {
        }
    }

    private static async Task CreateCollectionAsync(HttpClient client, string path)
    {
        using var created = await client.PutAsync(new Uri(path, UriKind.Relative), null);
        created.EnsureSuccessStatusCode();
        using var response = await client.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task WriteAsync(HttpClient client, string path, Faker faker)
    {
        foreach (var _ in Enumerable.Range(0, 60))
        {
            var id = $"doc-{faker.Random.Int(0, 19)}";
            if (faker.Random.Bool(0.25f))
            {
                await DeleteAsync(client, path, id);
            }
            else
            {
                await PutAsync(client, path, id, faker);
            }
        }
    }

    private static async Task PutAsync(HttpClient client, string path, string id, Faker faker)
    {
        using var response = await client.PutAsJsonAsync(
            new Uri($"{path}/collections/items/documents/by-key?id={ReplicationDocuments.Id(id)}", UriKind.Relative),
            new { title = faker.Commerce.ProductName(), price = faker.Random.Decimal(1, 100), note = faker.Lorem.Sentence() });
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeleteAsync(HttpClient client, string path, string id)
    {
        using var response = await client.DeleteAsync(new Uri($"{path}/collections/items/documents/by-key?id={ReplicationDocuments.Id(id)}", UriKind.Relative));
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    // HTTP verifies the live JSON; a read-only engine snapshot also covers tombstones and numeric offsets.
    private static async Task<List<(string Id, DocumentVersion? State)>> VersionsAsync(TerraBaseTestServer node, TenantContext database)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var store = await node.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token);
        await using var transaction = await store!.BeginTransactionAsync(write: false, deadline.Token);
        var versions = new List<(string Id, DocumentVersion? State)>();
        foreach (var id in Enumerable.Range(0, 20).Select(id => $"doc-{id}").Append("deleted"))
        {
            var state = await transaction.GetVersionAsync("items", ReplicationDocuments.Id(id), deadline.Token);
            versions.Add((id, state));
        }

        return versions;
    }

    private static void Configure(Dictionary<string, string> settings, PeerTls peerTls, ClientTls clientTls, int port, int peerPort)
    {
        peerTls.Apply(settings);
        clientTls.Apply(settings);
        settings["peer-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings["peer-addresses"] = $"https://localhost:{peerPort}";
    }

    // Each live document as "id json", in id order; opaque HTTP versions differ by node incarnation.
    private static async Task<List<string>> ListAsync(HttpClient client, string path)
    {
        using var page = JsonDocument.Parse(await client.GetStringAsync(new Uri($"{path}/collections/items/documents?limit=100", UriKind.Relative)));
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(document =>
            $"{document.GetProperty("id").GetString()} {document.GetProperty("document").GetRawText()}")];
    }

    // Two committed groups (the collection and one document) on disk before the sender starts, so its first
    // stream for the database carries groups. Returns the database id its Hello names.
    private static async Task<Google.Protobuf.ByteString> SeedAsync(ClientTls clientTls, string data)
    {
        await using var seed = await TerraBaseTestServer.StartAsync(settings =>
        {
            clientTls.Apply(settings);
            settings["data-directory"] = data;
        });
        var tenant = await ReplicationTenant.CreateAsync(seed, "race");
        using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
        await CreateCollectionAsync(writer, tenant.Path("race"));
        await PutAsync(writer, tenant.Path("race"), "held", new Faker());
        return ReplicationProtocol.CreateHello(tenant.Database("race"), new byte[16]).LocalIdentity.DatabaseId;
    }

    private static Task<TerraBaseTestServer> StartSenderAsync(PeerTls peerTls, string data, FakePeer peer, ProgressClock clock) =>
        TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            settings["data-directory"] = data;
            settings["peer-addresses"] = peer.Address.ToString();
        }, services => services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ReplicationSender>(provider, clock)));

    private static async Task<HeldStream> FirstAsync(System.Collections.Concurrent.ConcurrentQueue<HeldStream> streams)
    {
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!streams.IsEmpty), TimeSpan.FromSeconds(30));
        return streams.TryPeek(out var first) ? first : throw new InvalidOperationException("no stream was opened");
    }

    // The retry backoff runs on the same frozen clock: move it forward until the next attempt opens a stream.
    private static Task RetriedAsync(ProgressClock clock, System.Collections.Concurrent.ConcurrentQueue<HeldStream> streams) =>
        TerraBaseTestServer.EventuallyAsync(_ =>
        {
            if (streams.Count >= 2)
            {
                return Task.FromResult(true);
            }

            clock.Advance(TimeSpan.FromMilliseconds(100));
            return Task.FromResult(false);
        }, TimeSpan.FromSeconds(30));

    private static async Task<CommittedGroupBatch> NextGroupAsync(IAsyncStreamReader<SyncRequest> requests, CancellationToken cancellationToken) =>
        await requests.MoveNext(cancellationToken) && requests.Current.CommittedGroup is { } group
            ? group
            : throw new InvalidDataException("the sender did not send a committed group");

    // A database the test does not script replicates normally.
    private static async Task AcknowledgeAsync(
        Hello hello, IAsyncStreamReader<SyncRequest> requests, IServerStreamWriter<SyncResponse> responses, ServerCallContext context)
    {
        await responses.WriteAsync(FakePeer.Welcome(hello, out var member), context.CancellationToken);
        while (await requests.MoveNext(context.CancellationToken))
        {
            var batch = requests.Current.CommittedGroup;
            await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
        }
    }

    private sealed class HeldStream(ProgressClock.Deadline deadline)
    {
        public ProgressClock.Deadline Deadline { get; } = deadline;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CommittedGroupBatch> Group { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CommittedGroupBatch> Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Only the sender uses this clock, with one stream at a time for the test's single peer.
    // Each Hello binds its own deadline; anti-entropy and later attempts cannot contaminate its observations.
    private sealed class ProgressClock : FakeTimeProvider
    {
        private Deadline? _activeDeadline;

        public ProgressClock()
            : base(DateTimeOffset.UtcNow)
        {
        }

        public Deadline ActiveDeadline => Volatile.Read(ref _activeDeadline) ?? throw new InvalidOperationException("no sender deadline was armed");

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != ReplicationProtocol.ProgressTimeout || period != Timeout.InfiniteTimeSpan)
            {
                return base.CreateTimer(callback, state, dueTime, period);
            }

            var expired = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deadline = new Deadline(this, base.CreateTimer(value =>
            {
                expired.TrySetResult(GetUtcNow());
                callback(value);
            }, state, dueTime, period), expired);
            Volatile.Write(ref _activeDeadline, deadline);
            return deadline;
        }

        public sealed class Deadline(ProgressClock clock, ITimer timer, TaskCompletionSource<DateTimeOffset> expired) : ITimer
        {
            public System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset> Rearms { get; } = new();

            public TaskCompletionSource<DateTimeOffset> Expired { get; } = expired;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == ReplicationProtocol.ProgressTimeout)
                {
                    Rearms.Enqueue(clock.GetUtcNow());
                }

                return timer.Change(dueTime, period);
            }

            public void Dispose() => timer.Dispose();

            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}
