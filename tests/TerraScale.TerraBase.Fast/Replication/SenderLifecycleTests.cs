using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Storage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class SenderLifecycleTests
{
    [Test]
    public async Task A_failed_TLS_attempt_invalidates_caught_up_progress_and_recovers_without_new_writes()
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var reject = 0;
        var refused = NewSignal();
        var completed = NewSignal();
        IConnectionLifetimeFeature? connection = null;
        ulong applied = 0;
        ByteString? digest = null;
        await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            connection = context.GetHttpContext().Features.Get<IConnectionLifetimeFeature>();
            await responses.WriteAsync(Welcome(requests.Current.Hello, applied, out var member, digest), context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                applied = batch.EventId.Sequence;
                digest = batch.JournalDigest;
                await responses.WriteAsync(FakePeer.Ack(member, batch, applied), context.CancellationToken);
            }

            if (applied > 0)
            {
                completed.TrySetResult();
            }
        }, https =>
        {
            https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
            https.ClientCertificateValidation = (_, _, _) =>
            {
                if (Volatile.Read(ref reject) == 0)
                {
                    return true;
                }

                refused.TrySetResult();
                return false;
            };
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "backlog");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        await CreateAsync(tenant, writer);
        await PutAsync(tenant, writer, "first");
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(applied).IsEqualTo(2UL);

        Volatile.Write(ref reject, 1);
        connection!.Abort();
        await refused.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == 2);
        Volatile.Write(ref reject, 0);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await Assert.That(applied).IsEqualTo(2UL);
    }

    [Test]
    [Arguments("digest")]
    [Arguments("prefix")]
    [Arguments("origin")]
    [Arguments("identity")]
    [Arguments("region")]
    [Arguments("streamError")]
    public async Task An_invalid_Welcome_invalidates_previously_caught_up_progress(string invalid)
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var applied = 0UL;
        ByteString? digest = null;
        var reject = 0;
        var refused = NewSignal();
        await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            var hello = requests.Current.Hello;
            var welcome = Welcome(hello, applied, out var member, digest);
            if (Volatile.Read(ref reject) != 0)
            {
                switch (invalid)
                {
                    case "digest":
                        welcome.Welcome.Frontiers[0].JournalDigest = ByteString.CopyFrom(new byte[JournalDigest.Length]);
                        break;
                    case "prefix":
                        welcome.Welcome.Frontiers[0].DurableSequence = ulong.MaxValue;
                        break;
                    case "origin":
                        welcome.Welcome.Frontiers[0].Origin.ReplicaId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray(bigEndian: true));
                        break;
                    case "identity":
                        welcome.Welcome.LocalIdentity.MemberId = ByteString.Empty;
                        break;
                    case "region":
                        welcome.Welcome.LocalIdentity.Region = "invalid\nregion";
                        break;
                }

                await responses.WriteAsync(invalid == "streamError"
                    ? new SyncResponse { Error = new StreamError { Code = StreamErrorCode.StreamErrorStaleEpoch } }
                    : welcome, context.CancellationToken);
                refused.TrySetResult();
                return;
            }

            await responses.WriteAsync(welcome, context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                applied = batch.EventId.Sequence;
                digest = batch.JournalDigest;
                await responses.WriteAsync(FakePeer.Ack(member, batch, applied), context.CancellationToken);
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "backlog");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        await CreateAsync(tenant, writer);
        await PutAsync(tenant, writer, "first");
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await Assert.That(applied).IsEqualTo(2UL);
        Volatile.Write(ref reject, 1);
        await refused.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == 2);
        Volatile.Write(ref reject, 0);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await Assert.That(applied).IsEqualTo(2UL);
    }

    [Test]
    public async Task A_failed_database_backoff_does_not_delay_new_work_for_a_healthy_database()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var clock = new HeldRetryClock();
        ByteString? brokenDatabase = null;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            var hello = requests.Current.Hello;
            var welcome = FakePeer.Welcome(hello, out var member);
            if (hello.LocalIdentity.DatabaseId == brokenDatabase)
            {
                welcome.Welcome.ProtocolVersion.Major++;
                await responses.WriteAsync(welcome, context.CancellationToken);
                return;
            }

            await responses.WriteAsync(welcome, context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
                // Group 1 created the healthy collection; group 2 is the healthy document.
                if (batch.EventId.Sequence == 2)
                {
                    received.TrySetResult();
                }
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
        }, services => services.AddSingleton<TimeProvider>(clock));
        var tenant = await ReplicationTenant.CreateAsync(sender, "broken", "healthy");
        brokenDatabase = ReplicationProtocol.CreateHello(tenant.Database("broken"), new byte[16]).LocalIdentity.DatabaseId;
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        foreach (var name in new[] { "broken", "healthy" })
        {
            using var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path(name)}/collections", UriKind.Relative), new { name = "items" });
            collection.EnsureSuccessStatusCode();
        }

        using var broken = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("broken")}/collections/items/documents/{ReplicationDocuments.Id("b")}", UriKind.Relative), new { value = "broken" });
        broken.EnsureSuccessStatusCode();
        await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The retry timer is deliberately held. A fresh healthy commit must wake the peer worker itself.
        using var healthy = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("healthy")}/collections/items/documents/{ReplicationDocuments.Id("h")}", UriKind.Relative), new { value = "healthy" });
        healthy.EnsureSuccessStatusCode();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == 2);
    }

    private sealed class HeldRetryClock : TimeProvider
    {
        public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long GetTimestamp() => 0;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Scheduled.TrySetResult();
            }

            return timer;
        }
    }

    [Test]
    public async Task A_caught_up_sender_repairs_a_recreated_receiver_without_new_local_writes()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "backlog");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        await tenant.BindAsync(receiver);
        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        await CreateAsync(tenant, writer);
        await PutAsync(tenant, writer, "a");
        await PutAsync(tenant, writer, "b");
        await TerraBaseTestServer.ReplicatedAsync(sender);
        var sent = await sender.MetricAsync("terrabase_replication_sent_groups_total");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var database = tenant.Database("backlog");
        var engine = receiver.Services.GetRequiredService<IStorageEngine>();
        var source = await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token);
        long sourceHead;
        await using (var initialSourceRead = await source!.BeginTransactionAsync(write: false, deadline.Token))
        {
            sourceHead = await initialSourceRead.GetLastChangeSequenceAsync(deadline.Token);
        }

        var old = await engine.OpenDatabaseAsync(database, deadline.Token);
        await engine.DeleteDatabaseAsync(database, deadline.Token);
        await engine.CreateDatabaseAsync(database, deadline.Token);
        var fresh = await engine.OpenDatabaseAsync(database, deadline.Token);
        await Assert.That(fresh!.Incarnation).IsNotEqualTo(old!.Incarnation);
        // Zero lag from the old ACKs cannot complete this check: require a new sending attempt first.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await sender.MetricAsync("terrabase_replication_sent_groups_total", "", token) > sent);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        foreach (var id in new[] { "a", "b" })
        {
            using var response = await reader.GetAsync(new Uri($"{tenant.Path("backlog")}/collections/items/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative), deadline.Token);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            await Assert.That(body.RootElement.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(id);
        }

        await using var sourceRead = await source!.BeginTransactionAsync(write: false, deadline.Token);
        await using var targetRead = await fresh.BeginTransactionAsync(write: false, deadline.Token);
        await Assert.That(await sourceRead.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(sourceHead);
        await Assert.That(await targetRead.GetAppliedSequenceAsync(await sourceRead.GetOriginAsync(deadline.Token), deadline.Token)).IsEqualTo(sourceHead);
        await Assert.That(await sender.MetricAsync("terrabase_replication_sent_groups_total")).IsEqualTo(sent + sourceHead);
    }

    [Test]
    public async Task Startup_measures_the_whole_journal_before_the_peer_answers_and_across_turns()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-backlog-").FullName;
        try
        {
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                var tenant = await ReplicationTenant.CreateAsync(seed, "backlog");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                // 300 groups: the collection's creation and 299 writes.
                await CreateAsync(tenant, writer);
                for (var index = 0; index < 299; index++)
                {
                    await PutAsync(tenant, writer, "old");
                }
            }

            var firstHello = NewSignal();
            var secondHello = NewSignal();
            var allowFirst = NewSignal();
            var allowSecond = NewSignal();
            var secondWelcome = NewSignal();
            var allowSecondTransfer = NewSignal();
            var streams = 0;
            var groups = 0;
            ulong applied = 0;
            ByteString? appliedProof = null;
            await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
            {
                await requests.MoveNext(context.CancellationToken);
                var turn = Interlocked.Increment(ref streams);
                (turn == 1 ? firstHello : secondHello).TrySetResult();
                await (turn == 1 ? allowFirst : allowSecond).Task.WaitAsync(context.CancellationToken);
                await responses.WriteAsync(Welcome(requests.Current.Hello, applied, out var member, appliedProof), context.CancellationToken);
                if (turn > 1)
                {
                    secondWelcome.TrySetResult();
                    await allowSecondTransfer.Task.WaitAsync(context.CancellationToken);
                }
                while (await requests.MoveNext(context.CancellationToken))
                {
                    var batch = requests.Current.CommittedGroup;
                    Interlocked.Increment(ref groups);
                    applied = batch.EventId.Sequence;
                    appliedProof = batch.JournalDigest;
                    await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
                }
            });
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = peer.Address.ToString();
            });

            await firstHello.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Peer work can begin before the initial discovery pass completes. Keep Welcome gated
            // until the initialized metric proves that the complete persisted head has been measured.
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sender.MetricAsync("terrabase_replication_sender_initialized", "", token) == 1);
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(300);
            allowFirst.SetResult();
            await secondHello.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(groups).IsEqualTo(256);
            // The opening response has not yet proved the receiving incarnation or its prefix.
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == 300);
            allowSecond.SetResult();
            await secondWelcome.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token) == 44);
            allowSecondTransfer.SetResult();
            await TerraBaseTestServer.ReplicatedAsync(sender);
            await Assert.That(groups).IsEqualTo(300);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task Recreating_a_database_discards_the_old_heads_acknowledgments_and_pending_work()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var heldOld = new TaskCompletionSource<CommittedGroupBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldNew = new TaskCompletionSource<CommittedGroupBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = NewSignal();
        var releaseNew = NewSignal();
        var oldAckWritten = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new List<Task>();
        var finishing = false;
        Exception? primaryFailure = null;
        ByteString? oldOrigin = null;
        var prefixes = new Dictionary<ByteString, ulong>();
        var digests = new Dictionary<ByteString, ByteString>();
        await using var peer = await FakePeer.StartAsync(peerTls, (requests, responses, context) =>
        {
            lock (handlers)
            {
                if (finishing)
                {
                    return Task.CompletedTask;
                }

                var handler = HandleAsync();
                handlers.Add(handler);
                return handler;
            }

            async Task HandleAsync()
            {
                try
                {
                    await requests.MoveNext(context.CancellationToken);
                    var origin = requests.Current.Hello.LocalIdentity.MemberIncarnation;
                    await responses.WriteAsync(Welcome(requests.Current.Hello, prefixes.GetValueOrDefault(origin), out var member, digests.GetValueOrDefault(origin)), context.CancellationToken);
                    var wroteOldAck = false;
                    while (await requests.MoveNext(context.CancellationToken))
                    {
                        var batch = requests.Current.CommittedGroup;
                        oldOrigin ??= batch.EventId.Origin.Incarnation;
                        // Apply the group before holding only its acknowledgment.
                        prefixes[origin] = batch.EventId.Sequence;
                        digests[origin] = batch.JournalDigest;
                        if (!batch.EventId.Origin.Incarnation.Equals(oldOrigin))
                        {
                            heldNew.TrySetResult(batch);
                            await releaseNew.Task.WaitAsync(context.CancellationToken);
                        }
                        // Groups 1 to 17 are the collection's creation and the first 16 writes.
                        else if (batch.EventId.Sequence == 18)
                        {
                            heldOld.TrySetResult(batch);
                            await releaseOld.Task.WaitAsync(context.CancellationToken);
                        }

                        await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
                        if (batch.EventId.Origin.Incarnation.Equals(oldOrigin) && batch.EventId.Sequence == 18)
                        {
                            wroteOldAck = true;
                        }
                    }

                    if (wroteOldAck)
                    {
                        oldAckWritten.TrySetResult(null);
                    }
                }
                catch (Exception error)
                {
                    oldAckWritten.TrySetResult(error);
                    throw;
                }
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            // Writes must return while this fixture deliberately withholds acknowledgments.
            settings["write-wait-ms"] = "0";
        });
        try
        {
            var tenant = await ReplicationTenant.CreateAsync(sender, "backlog");
            using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
            await CreateAsync(tenant, writer);
            for (var index = 0; index < 16; index++)
            {
                await PutAsync(tenant, writer, "old");
            }

            await TerraBaseTestServer.ReplicatedAsync(sender);
            // Database deletion is exposed through Management, not the document route; drive the running
            // application's engine to cover deletion while the document writes still use the HTTPS listener.
            var database = tenant.Database("backlog");
            var engine = sender.Services.GetRequiredService<IStorageEngine>();
            var oldStore = await engine.OpenDatabaseAsync(database, CancellationToken.None);
            await PutAsync(tenant, writer, "old");
            await heldOld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // This already-open transaction can finish on the unlinked old file after recreation.
            var orphan = await oldStore!.BeginTransactionAsync(write: true, CancellationToken.None);
            await using var orphanScope = orphan;
            await orphan.PutAsync("items", ReplicationDocuments.Id("orphan"), "{}"u8.ToArray(), CancellationToken.None);
            await Assert.That(await engine.DeleteDatabaseAsync(database, CancellationToken.None)).IsTrue();

            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(0);
            await engine.CreateDatabaseAsync(database, CancellationToken.None);
            // The new database's groups: its collection's creation and the put.
            await CreateAsync(tenant, writer);
            await PutAsync(tenant, writer, "new");
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(2);
            await orphan.CommitAsync(CancellationToken.None);
            await orphan.DisposeAsync();
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(2);
            // Neither this old-origin ACK nor the old transaction's callback may revive retired state.
            releaseOld.SetResult();
            await Assert.That(await oldAckWritten.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsNull();
            var batch = await heldNew.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(batch.EventId.Sequence).IsEqualTo(1UL);
            await Assert.That(oldOrigin).IsNotNull();
            await Assert.That(batch.EventId.Origin.Incarnation.Span.SequenceEqual(oldOrigin!.Span)).IsFalse();
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(2);
            releaseNew.SetResult();
            await TerraBaseTestServer.ReplicatedAsync(sender);
            await Assert.That(prefixes[oldOrigin!]).IsEqualTo(18UL);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            Task[] pending;
            lock (handlers)
            {
                finishing = true;
                pending = [.. handlers];
            }

            releaseOld.TrySetResult();
            releaseNew.TrySetResult();
            await StoragePoolTests.JoinCleanupAsync(pending, primaryFailure);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_peer_waiting_on_Hello_or_an_ack_does_not_pin_the_SQLite_WAL(bool waitForAck)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var waiting = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewSignal();
        await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            if (!waitForAck)
            {
                waiting.TrySetResult(context.CancellationToken);
                await release.Task.WaitAsync(context.CancellationToken);
            }

            await responses.WriteAsync(FakePeer.Welcome(requests.Current.Hello, out var member), context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                if (waitForAck)
                {
                    waiting.TrySetResult(context.CancellationToken);
                    await release.Task.WaitAsync(context.CancellationToken);
                }

                var batch = requests.Current.CommittedGroup;
                await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
            }
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            // The backlog accumulates while the peer stalls; write-wait would hold each write on the stalled stream.
            settings["write-wait-ms"] = "0";
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "backlog");
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        await CreateAsync(tenant, writer);
        await PutAsync(tenant, writer, "first");
        var heldRpc = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 16; index++)
        {
            await PutAsync(tenant, writer, "later");
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = tenant.File(sender, "backlog"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 0;";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        long busy = -1;
        long frames = -1;
        // Discovery can briefly read the same WAL. The original held RPC must survive every attempt,
        // so a sender timeout cannot release a leaked transaction and falsely let the checkpoint pass.
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            heldRpc.ThrowIfCancellationRequested();
            await using var result = await command.ExecuteReaderAsync(token);
            await Assert.That(await result.ReadAsync(token)).IsTrue();
            busy = result.GetInt64(0);
            frames = result.GetInt64(1);
            heldRpc.ThrowIfCancellationRequested();
            return busy == 0;
        });
        await Assert.That(busy).IsEqualTo(0);
        await Assert.That(frames).IsEqualTo(0);
        heldRpc.ThrowIfCancellationRequested();

        release.SetResult();
        await TerraBaseTestServer.ReplicatedAsync(sender);
    }

    private static SyncResponse Welcome(Hello hello, ulong applied, out PeerIdentity member, ByteString? digest = null)
    {
        var welcome = FakePeer.Welcome(hello, out member);
        welcome.Welcome.Frontiers.Add(new PeerFrontier
        {
            Origin = new OriginIdentity { ReplicaId = hello.LocalIdentity.MemberIncarnation, Incarnation = hello.LocalIdentity.MemberIncarnation },
            DurableSequence = applied,
            JournalDigest = digest ?? ByteString.CopyFrom(JournalDigest.Initial(hello.LocalIdentity.MemberIncarnation.Span)),
        });
        return welcome;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task CreateAsync(ReplicationTenant tenant, HttpClient client)
    {
        using var response = await client.PutAsync(new Uri(tenant.Path("backlog"), UriKind.Relative), null);
        response.EnsureSuccessStatusCode();
        using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("backlog")}/collections", UriKind.Relative), new { name = "items" });
        collection.EnsureSuccessStatusCode();
    }

    private static async Task PutAsync(ReplicationTenant tenant, HttpClient client, string id)
    {
        using var response = await client.PutAsJsonAsync(new Uri($"{tenant.Path("backlog")}/collections/items/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative), new { value = id });
        response.EnsureSuccessStatusCode();
    }
}
