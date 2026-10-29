using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

/// <summary>Known copy refusals and fault races on real shared Push streams, with production storage guards.</summary>
public sealed class PeerStreamFaultIsolationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly ReplicaId Origin = ReplicaId.Of(1, 7, 0);

    [Test]
    public async Task A_known_faulted_frontier_has_no_response_and_the_next_healthy_batch_is_acknowledged()
    {
        await using var node = await Node.StartAsync();
        var source = node.Api.Server.Services.GetRequiredService<IChangeGroupSource>();
        var cursor = await source.AckedAsync(node.Database, ReplicaId.Of(99, 1, 0), CancellationToken.None)
            ?? throw new InvalidOperationException("the healthy source cursor was refused");
        await Assert.That(cursor.Incarnation).IsEqualTo(node.Pin(node.Database));
        var before = await source.HeadAsync(node.Database, cursor.Incarnation, CancellationToken.None)
            ?? throw new InvalidOperationException("the healthy source head was refused");
        await node.PutAsync(node.Database, "damaged");
        var changed = await (await node.StoreAsync(node.Database)).WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "UPDATE turso_cdc SET change_type = 7 WHERE table_name = '_tb_state' AND change_id > $after;";
            command.Parameters.AddWithValue("$after", before);
            return command.ExecuteNonQuery();
        }, "a test damaging the change log before an incoming frontier", CancellationToken.None);
        await Assert.That(changed).IsGreaterThan(0);
        try
        {
            var refused = await source.ReadAsync(node.Database, cursor.Incarnation, before,
                ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None);
            await Assert.That(refused).IsNull().Because("an established copy fault is refusal, never an empty successful read");
        }
        catch (Exception refusal) when (refusal is InvalidDataException or ReplicaCopyFaultedException)
        {
            await Assert.That(refusal is InvalidDataException or ReplicaCopyFaultedException).IsTrue()
                .Because("the first damaged read retains its decoder error; an already known fault may refuse");
        }
        await Assert.That(await node.Api.Server.Services.GetRequiredService<IRebuildLedger>()
            .GetAsync(node.Database, CancellationToken.None)).IsNotNull();

        using var deadline = Timeout();
        using var channel = PeerChannel.Create(node.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = await HelloAsync(new Features.Replication.V1.Replication.ReplicationClient(channel), deadline.Token);
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Frontier = new Frontier { Database = ChangeGroups.Wire(node.Database, node.Pin(node.Database)), Hlc = Stamp().Value },
        }, deadline.Token);
        await call.RequestStream.WriteAsync(Frame(node.Second, node.Pin(node.Second), 1, "healthy"), deadline.Token);
        // The very next frame must be the healthy ACK: neither a StableReport nor a batch ACK belongs to the frontier.
        var healthy = await AckAsync(call, deadline.Token);
        await Assert.That((healthy.Database, healthy.Refused, healthy.ChangeId))
            .IsEqualTo((ChangeGroups.Wire(node.Second, node.Pin(node.Second)), false, 1UL));
        await Assert.That(healthy.Applied).IsGreaterThan(0UL);
        await Assert.That(await node.HasAsync(node.Second, "healthy", deadline.Token)).IsTrue();

        await call.RequestStream.WriteAsync(Frame(node.Database, node.Pin(node.Database), 1, "refused"), deadline.Token);
        await AssertRefusedAsync(await AckAsync(call, deadline.Token), node.Database, node.Pin(node.Database));
        await Assert.That(await node.HasAsync(node.Database, "refused", deadline.Token)).IsFalse();
        await call.RequestStream.WriteAsync(Frame(node.Second, node.Pin(node.Second), 2, "still-healthy"), deadline.Token);
        await Assert.That((await AckAsync(call, deadline.Token)).ChangeId).IsEqualTo(2UL);
        await Assert.That(await node.HasAsync(node.Second, "still-healthy", deadline.Token)).IsTrue();
    }

    [Test]
    public async Task Thirty_two_faulted_frontiers_keep_heartbeats_alive_and_acknowledge_a_healthy_sentinel()
    {
        await using var node = await Node.StartAsync();
        using var deadline = Timeout();
        await node.FaultAsync(node.Database, nodeFault: false, deadline.Token);
        using var channel = PeerChannel.Create(node.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = await HelloAsync(new Features.Replication.V1.Replication.ReplicationClient(channel), deadline.Token);
        for (var index = 0; index < 32; index++)
        {
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Frontier = new Frontier { Database = ChangeGroups.Wire(node.Database, node.Pin(node.Database)), Hlc = Stamp().Value },
            }, deadline.Token);
            await call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() }, deadline.Token);
            await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.KindCase).IsEqualTo(AckFrame.KindOneofCase.None)
                .Because("the faulted frontier has no response; only the heartbeat gets an empty reply");
        }

        await call.RequestStream.WriteAsync(Frame(node.Second, node.Pin(node.Second), 1, "sentinel"), deadline.Token);
        var ack = await AckAsync(call, deadline.Token);
        await Assert.That((ack.Database, ack.Refused, ack.ChangeId))
            .IsEqualTo((ChangeGroups.Wire(node.Second, node.Pin(node.Second)), false, 1UL));
        await Assert.That(await node.HasAsync(node.Second, "sentinel", deadline.Token)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_fault_between_apply_and_stable_never_acknowledges_the_committed_batch(bool nodeFault)
    {
        Action<TenantContext>? afterApply = null;
        await using var node = await Node.StartAsync(services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step == "replication.apply.committed")
                {
                    afterApply?.Invoke(database);
                }
            }));
        // Admit forensic inspection before SYSTEM can fault; BEGIN DEFERRED takes no read snapshot yet.
        await using var forensic = await (await node.StoreAsync(node.Database)).BeginTransactionAsync(write: false, CancellationToken.None);
        Task? recording = null;
        afterApply = database =>
        {
            if (database == node.Database)
            {
                recording = node.FaultAsync(database, nodeFault, CancellationToken.None, "a test fault after apply and before stable");
            }
        };

        using var deadline = Timeout();
        using var channel = PeerChannel.Create(node.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = await HelloAsync(new Features.Replication.V1.Replication.ReplicationClient(channel), deadline.Token);
        await call.RequestStream.WriteAsync(Frame(node.Database, node.Pin(node.Database), 1, "landed"), deadline.Token);
        if (nodeFault)
        {
            _ = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(deadline.Token));
        }
        else
        {
            await AssertRefusedAsync(await AckAsync(call, deadline.Token), node.Database, node.Pin(node.Database));
            afterApply = null;
            await call.RequestStream.WriteAsync(Frame(node.Second, node.Pin(node.Second), 1, "healthy"), deadline.Token);
            await Assert.That((await AckAsync(call, deadline.Token)).ChangeId).IsEqualTo(1UL);
            await Assert.That(await node.HasAsync(node.Second, "healthy", deadline.Token)).IsTrue();
            // A refused stable read must not advance the receiver's last change ID: replay is a storage refusal,
            // not a malformed duplicate, and no successful merge observation was published for the first batch.
            await call.RequestStream.WriteAsync(Frame(node.Database, node.Pin(node.Database), 1, "landed"), deadline.Token);
            await AssertRefusedAsync(await AckAsync(call, deadline.Token), node.Database, node.Pin(node.Database));
            using var metrics = new HttpClient { BaseAddress = node.Api.Server.MetricsAddress };
            var text = await metrics.GetStringAsync(new Uri("/metrics", UriKind.Relative), deadline.Token);
            await Assert.That(text.Contains("terrabase_replication_undecodable_batches_total{", StringComparison.Ordinal)).IsFalse();
            var merged = text.Split('\n').Single(line => line.StartsWith("terrabase_replication_merged_groups_total{", StringComparison.Ordinal));
            await Assert.That(double.Parse(merged[(merged.LastIndexOf('}') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1);
        }

        await Assert.That(recording).IsNotNull();
        await recording!.WaitAsync(deadline.Token);
        await Assert.That(await forensic.GetAsync("items", "landed", deadline.Token) is { Deleted: false }).IsTrue()
            .Because("durable apply completed, but its subsequent stable read refused certification");
    }

    [Test]
    public async Task The_native_error_that_faults_the_node_during_stable_ends_the_stream_without_an_ACK()
    {
        Action<string, TenantContext>? checkpoint = null;
        await using var node = await Node.StartAsync(services: services =>
        {
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database));
            services.AddSingleton(provider => new HeldSource(provider.GetRequiredService<FaultAwareReplicaSource>()));
            services.AddSingleton<IReplicaStates>(provider => provider.GetRequiredService<HeldSource>());
        });
        var source = node.Api.Server.Services.GetRequiredService<HeldSource>();
        var faults = node.Api.Server.Services.GetRequiredService<IReplicaCopyFaults>();
        // Admit forensic inspection before SYSTEM can fault; BEGIN DEFERRED takes no read snapshot yet.
        await using var forensic = await (await node.StoreAsync(node.Database)).BeginTransactionAsync(write: false, CancellationToken.None);
        var native = new SqliteException("a native I/O error during the stable reader reset", 10);
        var armed = 0;
        Task? recording = null;
        checkpoint = (step, database) =>
        {
            if (database != node.Database)
            {
                return;
            }

            if (step == "replication.apply.committed")
            {
                Volatile.Write(ref armed, 1);
            }
            else if (step == "reader.reset" && Interlocked.Exchange(ref armed, 0) == 1)
            {
                recording = node.FaultAsync(node.Database, nodeFault: true, CancellationToken.None, "a node fault during the stable reader reset");
                throw native;
            }
        };

        using var deadline = Timeout();
        using var channel = PeerChannel.Create(node.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = await HelloAsync(new Features.Replication.V1.Replication.ReplicationClient(channel), deadline.Token);
        try
        {
            await call.RequestStream.WriteAsync(Frame(node.Database, node.Pin(node.Database), 1, "landed-native"), deadline.Token);
            var ended = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(deadline.Token));
            await Assert.That(ended!.StatusCode is not (StatusCode.Cancelled or StatusCode.DeadlineExceeded)).IsTrue()
                .Because("the server must end this call before the test deadline, without emitting an ACK");
            var (boundary, actualError) = await source.NativeReadError.Task.WaitAsync(deadline.Token);
            await Assert.That(boundary).IsEqualTo("stable");
            await Assert.That(ReferenceEquals(actualError, native)).IsTrue()
                .Because("the actual storage disposal error must reach the receiver without translation into a known refusal");
            await Assert.That(actualError.SqliteErrorCode).IsEqualTo(10);
            await Assert.That(recording).IsNotNull();
            await recording!.WaitAsync(deadline.Token);
            await Assert.That(await faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, deadline.Token)).IsTrue();
            await Assert.That(await forensic.GetAsync("items", "landed-native", deadline.Token) is { Deleted: false }).IsTrue()
                .Because("apply committed before stable failed, but neither a positive nor a refused ACK may follow the node fault");
        }
        finally
        {
            checkpoint = null;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_node_fault_on_a_frontier_or_batch_ends_the_shared_stream(bool batch)
    {
        await using var node = await Node.StartAsync();
        using var deadline = Timeout();
        using var channel = PeerChannel.Create(node.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = await HelloAsync(new Features.Replication.V1.Replication.ReplicationClient(channel), deadline.Token);
        // Admit forensic inspection before SYSTEM can fault; BEGIN DEFERRED takes no read snapshot yet.
        await using var forensic = await (await node.StoreAsync(node.Database)).BeginTransactionAsync(write: false, CancellationToken.None);
        await node.FaultAsync(node.Database, nodeFault: true, deadline.Token);
        var frame = batch ? Frame(node.Database, node.Pin(node.Database), 1, "refused") : new PushFrame
        {
            Frontier = new Frontier { Database = ChangeGroups.Wire(node.Database, node.Pin(node.Database)), Hlc = Stamp().Value },
        };
        await call.RequestStream.WriteAsync(frame, deadline.Token);
        _ = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(deadline.Token));
        await Assert.That(await forensic.GetAsync("items", "refused", deadline.Token) is { Deleted: false }).IsFalse();
    }

    [Test]
    public async Task Shape_proofs_pass_a_healthy_pin_and_refuse_stale_pins_faulted_copies_and_faulted_nodes()
    {
        await using var node = await Node.StartAsync();
        var source = node.Api.Server.Services.GetRequiredService<FaultAwareReplicaSource>();
        using var deadline = Timeout();
        await node.PutAsync(node.Database, "proved", deadline.Token);
        await node.PutAsync(node.Second, "proved", deadline.Token);
        var head = await source.HeadAsync(node.Database, node.Pin(node.Database), deadline.Token)
            ?? throw new InvalidOperationException("the healthy source head was refused");
        var digest = await source.DigestAsync(node.Database, node.Pin(node.Database), deadline.Token)
            ?? throw new InvalidOperationException("the healthy source digest was refused");
        var secondDigest = await source.DigestAsync(node.Second, node.Pin(node.Second), deadline.Token)
            ?? throw new InvalidOperationException("the healthy second digest was refused");
        var none = new HashSet<ReplicaId>();
        await Assert.That(await source.ProveAsync(node.Database, node.Pin(node.Database), 0, head, none, deadline.Token)).IsTrue();
        await Assert.That(await source.ProveAsync(node.Database, Ulid.NewUlid(), 0, head, none, deadline.Token)).IsFalse()
            .Because("a proof for another incarnation must not move this copy's remote durable frontier");

        await node.FaultAsync(node.Database, nodeFault: false, deadline.Token);
        await Assert.That(await source.ProveAsync(node.Database, node.Pin(node.Database), 0, head, none, deadline.Token)).IsFalse()
            .Because("an established copy fault refuses the proof instead of advancing a faulted frontier");
        await Assert.That(await source.MatchedAsync(node.Database, digest, deadline.Token)).IsFalse()
            .Because("equal-digest publication must refuse the faulted tenant copy too");
        var second = await source.HeadAsync(node.Second, node.Pin(node.Second), deadline.Token)
            ?? throw new InvalidOperationException("the healthy second head was refused");
        await Assert.That(await source.ProveAsync(node.Second, node.Pin(node.Second), 0, second, none, deadline.Token)).IsTrue()
            .Because("a tenant copy fault stays isolated to that tenant");

        await node.FaultAsync(node.Database, nodeFault: true, deadline.Token);
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await source.ProveAsync(node.Second, node.Pin(node.Second), 0, second, none, deadline.Token));
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await source.MatchedAsync(node.Second, secondDigest, deadline.Token));
    }

    [Test]
    public async Task A_cold_SYSTEM_cursor_refusal_keeps_tenant_replication_and_ACKs_progressing()
    {
        await using var receiver = await Node.StartAsync();
        await using var sender = await Node.StartAsync(receiver, services =>
        {
            services.AddSingleton(provider => new HeldSource(provider.GetRequiredService<FaultAwareReplicaSource>(), refuseSystemCursor: true));
            services.AddSingleton<IChangeGroupSource>(provider => provider.GetRequiredService<HeldSource>());
        }, receiver.Api.Server.BaseAddress);
        var source = sender.Api.Server.Services.GetRequiredService<HeldSource>();
        using var deadline = Timeout();
        await source.SystemCursorRefused.Task.WaitAsync(deadline.Token);
        CommitMarker marker;
        await using (var transaction = await (await sender.StoreAsync(sender.Database, deadline.Token)).BeginDurableTransactionAsync(deadline.Token))
        {
            _ = await transaction.PutAsync("items", "after-cold-system-refusal", "{\"n\":1}"u8.ToArray(), deadline.Token);
            marker = (await transaction.CommitAsync(deadline.Token)).Marker
                ?? throw new InvalidOperationException("the tenant write did not produce a commit marker");
        }
        await Assert.That(marker.ChangeId).IsGreaterThan(0);
        await TerraBaseTestServer.EventuallyAsync(async token =>
            sender.Status(sender.Database)?.Acknowledged >= marker.ChangeId
            && await receiver.HasAsync(receiver.Database, "after-cold-system-refusal", token), Deadline);

        var peer = receiver.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
        var production = sender.Api.Server.Services.GetRequiredService<FaultAwareReplicaSource>();
        // The stream ACK advances before #324's background writer persists the resume cursor.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await production.AckedAsync(sender.Database, peer, token) is { } cursor
            && cursor.Incarnation == marker.Incarnation && cursor.Acked >= marker.ChangeId, Deadline);
        var persisted = await production.AckedAsync(sender.Database, peer, deadline.Token)
            ?? throw new InvalidOperationException("the healthy tenant ACK cursor was refused");
        await Assert.That(persisted.Incarnation).IsEqualTo(marker.Incarnation);
        await Assert.That(persisted.Acked).IsGreaterThanOrEqualTo(marker.ChangeId);
        var systemStatus = sender.Status(ChangeGroups.System)
            ?? throw new InvalidOperationException("the cold SYSTEM cursor was not observed");
        await Assert.That((systemStatus.Incarnation, systemStatus.Acknowledged)).IsEqualTo((Ulid.Empty, 0L));
        var engine = sender.Api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(deadline.Token);
        await using var read = (TursoStorageTransaction)await system.BeginTransactionAsync(write: false, deadline.Token);
        await Assert.That((await read.GetAckedAsync(peer, system.Incarnation, deadline.Token))?.Acked ?? 0).IsEqualTo(0L)
            .Because("a refused SYSTEM cursor cannot publish an acknowledged source prefix");
        await Assert.That(await sender.Api.Server.Services.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(ChangeGroups.System, Ulid.Empty, deadline.Token)).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task In_flight_ACK_and_stable_bookkeeping_refusals_preserve_truthful_cursors(bool stableReport, bool nodeFault)
    {
        await using var receiver = await Node.StartAsync();
        HeldSource? held = null;
        await using var sender = await Node.StartAsync(receiver, services =>
            services.AddSingleton<IChangeGroupSource>(provider => held = new HeldSource(provider.GetRequiredService<FaultAwareReplicaSource>())),
            receiver.Api.Server.BaseAddress);
        var source = held ?? throw new InvalidOperationException("the sender source was not activated");
        using var deadline = Timeout();
        await WarmAsync(sender, receiver, deadline.Token);
        var before = sender.Status(sender.Database)!.Acknowledged;
        var connected = source.Connections;
        var gate = new BookkeepingGate(sender.Database, stableReport, deadline.Token);
        source.Hold(gate);
        try
        {
            if (!stableReport)
            {
                await sender.PutAsync(sender.Database, "in-flight", deadline.Token);
            }

            var changeId = await gate.Entered.Task.WaitAsync(deadline.Token);
            if (stableReport)
            {
                await Assert.That(sender.Status(sender.Database)!.Acknowledged).IsEqualTo(before)
                    .Because("a stable report cannot advance the ACK cursor");
            }
            else
            {
                var peer = receiver.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
                await Assert.That((await source.AckedAsync(sender.Database, peer, deadline.Token))!.Value.Acked).IsLessThan(changeId)
                    .Because("background bookkeeping cannot advance the persisted resume cursor while it is pending");
            }
            if (!stableReport)
            {
                await Assert.That(changeId).IsGreaterThan(before);
                await Assert.That(await receiver.HasAsync(receiver.Database, "in-flight", deadline.Token)).IsTrue();
            }

            await sender.FaultAsync(sender.Database, nodeFault, deadline.Token);
            _ = gate.Release.TrySetResult();
            await gate.Refused.Task.WaitAsync(deadline.Token);
            if (nodeFault)
            {
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(source.Connections > connected), Deadline);
                await Assert.That(sender.Status(sender.Database)?.Acknowledged ?? 0).IsLessThanOrEqualTo(before)
                    .Because("a node failure ends the call and cannot publish the completed response as a successful ACK");
            }
            else
            {
                await sender.PutAsync(sender.Second, "after-refusal", deadline.Token);
                var head = await sender.Api.Server.Services.GetRequiredService<IChangeGroupSource>().HeadAsync(sender.Second, sender.Pin(sender.Second), deadline.Token)
                    ?? throw new InvalidOperationException("the healthy source head was refused");
                await TerraBaseTestServer.EventuallyAsync(async token =>
                    sender.Status(sender.Second)?.Acknowledged == head
                    && await receiver.HasAsync(receiver.Second, "after-refusal", token), Deadline);
                await Assert.That(source.Connections).IsEqualTo(connected)
                    .Because("a refused tenant report or ACK must not restart the shared connection");
                await Assert.That(sender.Status(sender.Database)!.Acknowledged).IsEqualTo(stableReport ? before : 0)
                    .Because("a stable report changes no cursor; refused ACK bookkeeping resets its cursor to neutral");
                await Assert.That(await sender.Api.Server.Services.GetRequiredService<IRebuildLedger>()
                    .GetAsync(sender.Database, deadline.Token)).IsNotNull();
            }
        }
        finally
        {
            _ = gate.Release.TrySetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_busy_ACK_writer_does_not_replay_confirmed_groups_on_the_same_stream(bool stableReport)
    {
        await using var receiver = await Node.StartAsync();
        HeldSource? held = null;
        await using var sender = await Node.StartAsync(receiver, services =>
            services.AddSingleton<IChangeGroupSource>(provider => held = new HeldSource(provider.GetRequiredService<FaultAwareReplicaSource>())),
            receiver.Api.Server.BaseAddress);
        var source = held ?? throw new InvalidOperationException("the sender source was not activated");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        await WarmAsync(sender, receiver, deadline.Token);
        var connected = source.Connections;
        var confirmed = sender.Status(sender.Database)!.Acknowledged;
        var gate = new BookkeepingGate(sender.Database, stableReport, deadline.Token) { Busy = true, After = confirmed };
        source.Hold(gate);
        try
        {
            if (!stableReport)
            {
                await sender.PutAsync(sender.Database, "confirmed-before-busy", deadline.Token);
                confirmed = await gate.Entered.Task.WaitAsync(deadline.Token);
                await Assert.That(await receiver.HasAsync(receiver.Database, "confirmed-before-busy", deadline.Token)).IsTrue();
            }
            else
            {
                // Stable-only bookkeeping has no pending ACK; this case verifies that its failure stays neutral.
                _ = await gate.Entered.Task.WaitAsync(deadline.Token);
            }
            _ = gate.Release.TrySetResult();
            await gate.Refused.Task.WaitAsync(deadline.Token);
            // Only a busy writer failed: its old persisted ACK must not rewind this live stream.
            await sender.PutAsync(sender.Database, "after-busy", deadline.Token);
            await TerraBaseTestServer.EventuallyAsync(async token =>
                sender.Status(sender.Database)?.Acknowledged > confirmed
                && await receiver.HasAsync(receiver.Database, "after-busy", token), RemoteAckWaiter.Bound);
            if (!stableReport)
            {
                await Assert.That(gate.MinimumRead).IsLessThan(long.MaxValue);
                await Assert.That(gate.MinimumRead).IsGreaterThanOrEqualTo(confirmed)
                    .Because("no source read may replay rows already confirmed on this connection");
            }
            await Assert.That(source.Connections).IsEqualTo(connected)
                .Because("a tenant's writer contention must not restart the shared connection");
        }
        finally
        {
            _ = gate.Release.TrySetResult();
        }
    }

    [Test]
    [Arguments("read", false)]
    [Arguments("head", false)]
    [Arguments("retainedAfter", false)]
    [Arguments("horizon", false)]
    [Arguments("read", true)]
    [Arguments("head", true)]
    [Arguments("retainedAfter", true)]
    [Arguments("horizon", true)]
    public async Task A_fault_after_a_shipping_read_is_isolated_unless_it_affects_the_node(string boundary, bool nodeFault)
    {
        await using var receiver = await Node.StartAsync();
        await using var sender = await Node.StartAsync(receiver, services =>
        {
            services.AddSingleton(provider => new HeldSource(provider.GetRequiredService<FaultAwareReplicaSource>()));
            services.AddSingleton<IChangeGroupSource>(provider => provider.GetRequiredService<HeldSource>());
            services.AddSingleton<IReplicaStates>(provider => provider.GetRequiredService<HeldSource>());
        }, receiver.Api.Server.BaseAddress);
        var source = sender.Api.Server.Services.GetRequiredService<HeldSource>();
        using var deadline = Timeout();
        await WarmAsync(sender, receiver, deadline.Token);
        var before = sender.Status(sender.Database)!.Acknowledged;
        var connected = source.Connections;
        var gate = new ShippingGate(sender.Database, boundary);
        source.Hold(gate);
        try
        {
            await sender.PutAsync(sender.Database, "shipping-held", deadline.Token);
            await gate.Entered.Task.WaitAsync(deadline.Token);
            await Assert.That(sender.Status(sender.Database)!.Acknowledged).IsEqualTo(before);
            await sender.FaultAsync(sender.Database, nodeFault, deadline.Token);
            _ = gate.Release.TrySetResult();
            await gate.Refused.Task.WaitAsync(deadline.Token);
            if (nodeFault)
            {
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(source.Connections > connected), Deadline);
                await Assert.That(sender.Status(sender.Database)?.Acknowledged ?? 0).IsLessThanOrEqualTo(before)
                    .Because("a node-wide shipping refusal ends the shared call");
            }
            else
            {
                await sender.PutAsync(sender.Second, "after-shipping-refusal", deadline.Token);
                var head = await source.HeadAsync(sender.Second, sender.Pin(sender.Second), deadline.Token)
                    ?? throw new InvalidOperationException("the healthy source head was refused");
                await TerraBaseTestServer.EventuallyAsync(async token =>
                    sender.Status(sender.Second)?.Acknowledged == head
                    && await receiver.HasAsync(receiver.Second, "after-shipping-refusal", token), Deadline);
                await Assert.That(source.Connections).IsEqualTo(connected)
                    .Because("the healthy sentinel must be acknowledged on the original shared call");
                await Assert.That(sender.Status(sender.Database)!.Acknowledged).IsEqualTo(before)
                    .Because("a refused shipping read cannot acknowledge a new tenant prefix");
                await Assert.That(await sender.Api.Server.Services.GetRequiredService<IRebuildLedger>()
                    .GetAsync(sender.Database, deadline.Token)).IsNotNull();
            }

            await Assert.That(await receiver.HasAsync(receiver.Database, "shipping-held", deadline.Token)).IsFalse();
        }
        finally
        {
            _ = gate.Release.TrySetResult();
        }
    }

    [Test]
    public async Task The_native_error_that_faults_the_node_during_shipping_ends_the_shared_call()
    {
        await using var receiver = await Node.StartAsync();
        Action<string, TenantContext>? checkpoint = null;
        await using var sender = await Node.StartAsync(receiver, services =>
        {
            // The ACK tail gauge stays cached while the shipping read owns the reset injection; reconnect timers still run.
            services.AddSingleton<TimeProvider>(new FixedUtcTime());
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database));
            services.AddSingleton(provider => new HeldSource(provider.GetRequiredService<FaultAwareReplicaSource>()));
            services.AddSingleton<IChangeGroupSource>(provider => provider.GetRequiredService<HeldSource>());
            services.AddSingleton<IReplicaStates>(provider => provider.GetRequiredService<HeldSource>());
        }, receiver.Api.Server.BaseAddress);
        var source = sender.Api.Server.Services.GetRequiredService<HeldSource>();
        var faults = sender.Api.Server.Services.GetRequiredService<IReplicaCopyFaults>();
        using var deadline = Timeout();
        await WarmAsync(sender, receiver, deadline.Token);
        var before = sender.Status(sender.Database)!.Acknowledged;
        var connected = source.Connections;
        var native = new SqliteException("a native I/O error during the shipping reader reset", 10);
        var gate = new ShippingGate(sender.Database, "reading");
        source.Hold(gate);
        var armed = 1;
        Task? recording = null;
        try
        {
            await sender.PutAsync(sender.Database, "shipping-native", deadline.Token);
            await gate.Entered.Task.WaitAsync(deadline.Token);
            await Assert.That(sender.Status(sender.Database)!.Acknowledged).IsEqualTo(before);
            checkpoint = (step, database) =>
            {
                if (step == "reader.reset" && database == sender.Database && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    recording = sender.FaultAsync(sender.Database, nodeFault: true, CancellationToken.None, "a node fault during the shipping reader reset");
                    throw native;
                }
            };
            _ = gate.Release.TrySetResult();
            var (boundary, actualError) = await source.NativeReadError.Task.WaitAsync(deadline.Token);
            await Assert.That(boundary).IsEqualTo("reading");
            await Assert.That(ReferenceEquals(actualError, native)).IsTrue()
                .Because("shipping must see the original error from real read transaction disposal");
            await Assert.That(actualError.SqliteErrorCode).IsEqualTo(10);
            await Assert.That(recording).IsNotNull();
            await recording!.WaitAsync(deadline.Token);
            await Assert.That(await faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, deadline.Token)).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(source.Connections > connected), Deadline);
            await Assert.That(source.ReadsAfterNativeError).IsEqualTo(0)
                .Because("this call must end at the triggering error, without another tenant read to discover the known node fault");
            await Assert.That(sender.Status(sender.Database)?.Acknowledged ?? 0).IsLessThanOrEqualTo(before)
                .Because("the triggering native node error ends this call instead of holding only this tenant");
            await Assert.That(await receiver.HasAsync(receiver.Database, "shipping-native", deadline.Token)).IsFalse();
        }
        finally
        {
            checkpoint = null;
            _ = gate.Release.TrySetResult();
        }
    }

    // The receiver's real drop of 2,048 documents waits at each 512-document checkpoint for eleven seconds of fake time:
    // 44 seconds in all, past two deadlines. Another database ships every half second, so the link's writer is never quiet
    // for a whole heartbeat period, yet each checkpoint's growth reaches the sender and the batch stays in flight on one stream.
    [Test]
    public async Task A_replicated_drop_longer_than_the_deadline_reports_progress_and_commits_on_one_stream()
    {
        const int documents = 2048;
        var half = TimeSpan.FromMilliseconds(500);
        var time = new FakeTimeProvider();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var reached = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        using var observed = new SemaphoreSlim(0);
        TenantContext? dropping = null;
        await using var receiver = await Node.StartAsync(services: services =>
        {
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreamReceiver>(provider, time));
            // Only this tenant's drop waits, on its own writer; the stream loop keeps reading heartbeats and other databases.
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step == "storage.drop.progress" && database == Volatile.Read(ref dropping))
                {
                    _ = reached.Release();
                    release.Wait(deadline.Token);
                }
            });
        });
        HeldSource? held = null;
        await using var sender = await Node.StartAsync(receiver, services =>
        {
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreams>(provider, time));
            services.AddSingleton<IChangeGroupSource>(provider => held = new HeldSource(provider.GetRequiredService<FaultAwareReplicaSource>()));
            // Called on the sender's response reader: count only, never block.
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step == "replication.progress.observed" && database == Volatile.Read(ref dropping))
                {
                    _ = observed.Release();
                }
            });
        }, receiver.Api.Server.BaseAddress);
        var source = held ?? throw new InvalidOperationException("the sender source was not activated");
        // Whole seconds only until the drop, so heartbeat ticks stay on whole seconds; progress covers slow seeding applies.
        await WarmAsync(sender, receiver, deadline.Token, () => time.Advance(TimeSpan.FromSeconds(1)));
        var store = await sender.StoreAsync(sender.Database, deadline.Token);
        var ids = Enumerable.Range(0, documents).Select(_ => EntityIds.New().ToString()).ToArray();
        foreach (var chunk in ids.Chunk(IStorageTransaction.MaxChangeGroupChanges))
        {
            await using var transaction = await store.BeginDurableTransactionAsync(deadline.Token);
            foreach (var id in chunk)
            {
                _ = await transaction.PutAsync("items", id, "{\"n\":1}"u8.ToArray(), deadline.Token);
            }

            _ = await transaction.CommitAsync(deadline.Token);
        }

        await UntilAckedAsync(sender.Database, () => time.Advance(TimeSpan.FromSeconds(1)));
        await Assert.That(await receiver.HasAsync(receiver.Database, ids[^1], deadline.Token)).IsTrue();
        var connected = source.Connections;
        var acknowledged = sender.Status(sender.Database)!.Acknowledged;
        var peer = receiver.Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        Volatile.Write(ref dropping, sender.Database);
        CommitMarker marker;
        await using (var transaction = await store.BeginDurableTransactionAsync(deadline.Token))
        {
            await Assert.That(await transaction.DropCollectionAsync("items", deadline.Token)).IsTrue();
            marker = (await transaction.CommitAsync(deadline.Token)).Marker ?? throw new InvalidOperationException("the drop produced no commit marker");
        }

        var shipped = time.GetUtcNow();
        try
        {
            for (var checkpoint = 0; checkpoint < documents / 512; checkpoint++)
            {
                await reached.WaitAsync(deadline.Token);
                for (var second = 0; second < 11; second++)
                {
                    time.Advance(half);
                    await sender.PutAsync(sender.Second, $"busy-{checkpoint}-{second}", deadline.Token);
                    await UntilAckedAsync(sender.Second);
                    time.Advance(half);
                    if (second == 0)
                    {
                        await observed.WaitAsync(deadline.Token);
                    }
                }

                // Progress is liveness only: no stream ACK, persisted cursor or confirmation for the drop yet.
                await Assert.That(sender.Status(sender.Database)!.Acknowledged).IsEqualTo(acknowledged);
                await Assert.That((await source.AckedAsync(sender.Database, peer, deadline.Token))!.Value.Acked).IsLessThan(marker.ChangeId);
                await Assert.That(source.Connections).IsEqualTo(connected);
                await Assert.That(observed.CurrentCount).IsEqualTo(0).Because("unchanged work at a held checkpoint must not report progress twice");
                _ = release.Release();
            }

            // Completing the final JoinAsync adds work after the last drop checkpoint; a heartbeat may report it before the ACK.
            await UntilAckedAsync(sender.Database);
            await Assert.That(sender.Status(sender.Database)!.Acknowledged).IsGreaterThanOrEqualTo(marker.ChangeId);
            await Assert.That(time.GetUtcNow() - shipped).IsGreaterThan(2 * ReplicationProtocol.ProgressTimeout);
            await Assert.That(source.Connections).IsEqualTo(connected).Because("progress kept the long batch on its stream");
            await using var read = await (await receiver.StoreAsync(receiver.Database, deadline.Token)).BeginTransactionAsync(write: false, deadline.Token);
            await Assert.That(await read.GetCollectionAsync("items", deadline.Token)).IsNull();
        }
        finally
        {
            Volatile.Write(ref dropping, null);
            _ = release.Release(documents / 512);
        }

        // The clock stays put while waiting, so a wait alone cannot move a deadline or a heartbeat.
        async Task UntilAckedAsync(TenantContext database, Action? tick = null)
        {
            var head = await source.HeadAsync(database, sender.Pin(database), deadline.Token)
                ?? throw new InvalidOperationException("the source head was refused");
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                tick?.Invoke();
                return Task.FromResult(sender.Status(database)?.Acknowledged >= head);
            }, Deadline);
        }
    }

    private static async Task WarmAsync(Node sender, Node receiver, CancellationToken cancellationToken, Action? tick = null)
    {
        var source = sender.Api.Server.Services.GetRequiredService<IChangeGroupSource>();
        foreach (var database in new[] { sender.Database, sender.Second })
        {
            await sender.PutAsync(database, "warm", cancellationToken);
            var head = await source.HeadAsync(database, sender.Pin(database), cancellationToken)
                ?? throw new InvalidOperationException("the warm source head was refused");
            await Assert.That(head).IsGreaterThan(0);
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                tick?.Invoke();
                return sender.Status(database)?.Acknowledged == head && await receiver.HasAsync(database, "warm", token);
            }, Deadline);
        }
    }

    private static CancellationTokenSource Timeout()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(Deadline);
        return deadline;
    }

    private static Hlc Stamp() => new((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);

    private static PushFrame Frame(TenantContext database, Ulid incarnation, long changeId, string id)
    {
        var at = new EntryVersion(Stamp(), Origin);
        using var body = JsonDocument.Parse("""{"n":1}""");
        var delta = new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
        return new PushFrame
        {
            Batch = new Batch { Database = ChangeGroups.Wire(database, incarnation), LastChangeId = (ulong)changeId, Groups = ChangeGroups.Encode([new OriginGroup(changeId, [delta])]) },
        };
    }

    private static async Task<AsyncDuplexStreamingCall<PushFrame, AckFrame>> HelloAsync(
        Features.Replication.V1.Replication.ReplicationClient client, CancellationToken cancellationToken)
    {
        var call = client.Push(cancellationToken: cancellationToken);
        try
        {
            await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = Origin.Value, Region = "sjc" } }, cancellationToken);
            await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
            return call;
        }
        catch
        {
            call.Dispose();
            throw;
        }
    }

    private static async Task<Ack> AckAsync(AsyncDuplexStreamingCall<PushFrame, AckFrame> call, CancellationToken cancellationToken)
    {
        await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack).IsNotNull();
        return call.ResponseStream.Current.Ack;
    }

    private static async Task AssertRefusedAsync(Ack ack, TenantContext database, Ulid incarnation)
    {
        await Assert.That((ack.Database, ack.Refused, ack.ChangeId, ack.Applied, ack.Stable))
            .IsEqualTo((ChangeGroups.Wire(database, incarnation), true, 0UL, 0UL, 0UL));
    }

    private sealed class Node(ManagementApi api, TenantContext database, TenantContext second) : IAsyncDisposable
    {
        public ManagementApi Api { get; } = api;
        public TenantContext Database { get; } = database;
        public TenantContext Second { get; } = second;
        private readonly Dictionary<TenantContext, Ulid> _pins = [];
        private readonly HashSet<string> _expectedRetentionRefusals = [];

        public Ulid Pin(TenantContext database) => _pins[database];

        public static async Task<Node> StartAsync(Node? of = null, Action<IServiceCollection>? services = null, Uri? peer = null)
        {
            var database = of?.Database ?? new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
            var second = of?.Second ?? database with { DatabaseId = EntityIds.New().ToString() };
            var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "gru";
                settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
                if (peer is not null)
                {
                    settings["peer-addresses"] = peer.ToString();
                }
            }, collection =>
            {
                collection.AddSingleton<IReplicaRebuild, BusyRebuild>();
                services?.Invoke(collection);
            }, userId: of?.Api.User);
            var node = new Node(api, database, second);
            try
            {
                if (of is null)
                {
                    using var service = api.Service(api.User);
                    await CatalogFixture.RegisterAsync(api, api.User, database, "first", []);
                    await CatalogFixture.RegisterAsync(api, api.User, second, "second", []);
                }

                foreach (var tenant in new[] { database, second })
                {
                    // The sender's startup gate pulls the receiver's catalog; it must not recreate those identities.
                    await TerraBaseTestServer.EventuallyAsync(async token =>
                        await api.Server.Services.GetRequiredService<ITenantDirectory>().RegisteredAsync(tenant, token) is true, Deadline);
                    await Assert.That(await api.Server.Services.GetRequiredService<ITenantDirectory>()
                        .EnsureLocalDatabaseAsync(tenant, CancellationToken.None) is TerraBase.Errors.Done).IsTrue();
                    var collection = new CollectionInfo("items", 1);
                    if (of is not null)
                    {
                        await using var read = await (await of.StoreAsync(tenant)).BeginTransactionAsync(write: false, CancellationToken.None);
                        collection = await read.GetCollectionAsync("items", CancellationToken.None)
                            ?? throw new InvalidOperationException("the receiver collection is missing");
                    }

                    var store = await node.StoreAsync(tenant);
                    node._pins.Add(tenant, store.Incarnation);
                    await using var transaction = await store.BeginDurableTransactionAsync(CancellationToken.None);
                    _ = await transaction.CreateCollectionAsync(collection, CancellationToken.None);
                    _ = await transaction.CommitAsync(CancellationToken.None);
                }

                return node;
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }
        }

        public async Task<TursoDatabaseStore> StoreAsync(TenantContext database, CancellationToken cancellationToken = default) =>
            (TursoDatabaseStore)(await Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("the test database is missing"));

        public async Task PutAsync(TenantContext database, string id, CancellationToken cancellationToken = default)
        {
            await using var transaction = await (await StoreAsync(database, cancellationToken)).BeginDurableTransactionAsync(cancellationToken);
            _ = await transaction.PutAsync("items", id, "{\"n\":1}"u8.ToArray(), cancellationToken);
            _ = await transaction.CommitAsync(cancellationToken);
        }

        public async Task<bool> HasAsync(TenantContext database, string id, CancellationToken cancellationToken)
        {
            await using var transaction = await (await StoreAsync(database, cancellationToken)).BeginTransactionAsync(write: false, cancellationToken);
            return await transaction.GetAsync("items", id, cancellationToken) is { Deleted: false };
        }

        public async Task FaultAsync(
            TenantContext database, bool nodeFault, CancellationToken cancellationToken, string reason = "a test fault during replication bookkeeping")
        {
            var incarnation = nodeFault ? Ulid.Empty : (await StoreAsync(database, cancellationToken)).Incarnation;
            await Api.Server.Services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(nodeFault ? ChangeGroups.System : database,
                incarnation, reason, cancellationToken);
            _ = _expectedRetentionRefusals.Add(nodeFault
                ? $"storage failed closed: the system database faulted: {reason}"
                : $"this copy is faulted: {reason}");
        }

        public ReplicationDatabaseStatus? Status(TenantContext database) => Api.Server.Services.GetRequiredService<IReplicationStatus>()
            .Snapshot().Peers.SelectMany(peer => peer.Databases).SingleOrDefault(tenant => tenant.DatabaseId == database.DatabaseId);

        public async ValueTask DisposeAsync()
        {
            var retention = Api.Server.Services.GetRequiredService<TursoChangeGroupSource>();
            try
            {
                await Api.DisposeAsync();
                await Api.CleanupCompletion;
            }
            catch (ReplicaCopyFaultedException error) when (_expectedRetentionRefusals.Contains(error.Message)
                && retention.ExecuteTask?.Exception?.Flatten().InnerExceptions.Contains(error) == true)
            {
                // Shutdown can cancel retention's retry catch while it observes this fixture's injected copy or node fault.
            }
        }
    }

    private sealed class FixedUtcTime : TimeProvider
    {
        private readonly DateTimeOffset _now = System.GetUtcNow();
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Busy());
    }

    private sealed class BookkeepingGate(TenantContext database, bool stableReport, CancellationToken cancellationToken)
    {
        public bool Busy { get; init; }
        public long After { get; init; }
        public long MinimumRead { get; set; } = long.MaxValue;
        public TenantContext Database { get; } = database;
        public bool StableReport { get; } = stableReport;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<long> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ShippingGate(TenantContext database, string boundary)
    {
        public TenantContext Database { get; } = database;
        public string Boundary { get; } = boundary;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Only the await boundary is held; production FaultAwareReplicaSource makes the refusal using the real ledger.
    private sealed class HeldSource(FaultAwareReplicaSource inner, bool refuseSystemCursor = false) : IChangeGroupSource, IReplicaStates
    {
        public ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken) =>
            inner.MatchedAsync(database, digest, cancellationToken);

        private BookkeepingGate? _held;
        private BookkeepingGate? _busy;
        private ShippingGate? _shipping;
        private int _connections;
        private int _nativeConnection = -1;
        private int _readsAfterNativeError;
        public int Connections => Volatile.Read(ref _connections);
        public int ReadsAfterNativeError => Volatile.Read(ref _readsAfterNativeError);
        public TaskCompletionSource SystemCursorRefused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(string Boundary, SqliteException Error)> NativeReadError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Hold(BookkeepingGate gate) => Volatile.Write(ref _held, gate);
        public void Hold(ShippingGate gate) => Volatile.Write(ref _shipping, gate);

        public IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken) => inner.ListDatabasesAsync(cancellationToken);
        public Task ChangedAsync(CancellationToken cancellationToken) => inner.ChangedAsync(cancellationToken);
        public IDisposable Subscribe(Action<TenantContext> changed)
        {
            // The sender subscribes once after each welcome, before any database's placement or cursor read can refuse.
            _ = Interlocked.Increment(ref _connections);
            return inner.Subscribe(changed);
        }

        public async ValueTask<SourceGroups?> ReadAsync(TenantContext database, Ulid incarnation, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
        {
            // This fixture has one sender loop; retain only the minimum, never a growing read history.
            if (Volatile.Read(ref _busy) is { } busy && busy.Database == database)
            {
                busy.MinimumRead = Math.Min(busy.MinimumRead, after);
            }
            if (Volatile.Read(ref _nativeConnection) == Connections)
            {
                _ = Interlocked.Increment(ref _readsAfterNativeError);
            }

            await AfterShippingReadAsync(database, "reading", cancellationToken);
            var groups = await ShippingReadAsync(database, "reading", () => inner.ReadAsync(database, incarnation, after, maxGroups, maxBytes, cancellationToken));
            var gate = Volatile.Read(ref _shipping);
            if (groups is not null && gate is not null && gate.Database == database && gate.Boundary is "head" or "retainedAfter" or "horizon")
            {
                // Force only this test's refill branch; every storage call still passes through the production guard.
                groups = groups with { Retained = false };
            }

            // After the read, only the sender's own fault check stands before the batch: it refuses without a source call.
            if (gate is not null && gate.Database == database && gate.Boundary == "read" && gate.Entered.TrySetResult())
            {
                await gate.Release.Task.WaitAsync(cancellationToken);
                _ = Interlocked.CompareExchange(ref _shipping, null, gate);
                _ = gate.Refused.TrySetResult();
            }

            await AfterShippingReadAsync(database, "head", cancellationToken);
            return groups;
        }

        public ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken) =>
            inner.RetainsAsync(database, incarnation, after, cancellationToken);

        public async ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken)
        {
            var head = await ShippingReadAsync(database, "head", () => inner.HeadAsync(database, incarnation, cancellationToken));
            await AfterShippingReadAsync(database, "retainedAfter", cancellationToken);
            return head;
        }

        public async ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid incarnation, long? through, long head, CancellationToken cancellationToken)
        {
            var retained = await ShippingReadAsync(database, "retainedAfter", () => inner.RetainedAfterAsync(database, incarnation, through, head, cancellationToken));
            await AfterShippingReadAsync(database, "horizon", cancellationToken);
            return retained;
        }

        public ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken) => inner.DigestAsync(database, expected, cancellationToken);
        public ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken) =>
            inner.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, cancellationToken);
        public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken) =>
            ShippingReadAsync(database, "stable", () => inner.StableAsync(database, cancellationToken));
        public ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken cancellationToken) =>
            ShippingReadAsync(database, "horizon", () => inner.HorizonAsync(database, cancellationToken));
        public ValueTask<int?> ForgetAsync(TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
            inner.ForgetAsync(database, incarnation, keys, horizon, cancellationToken);

        private async ValueTask AfterShippingReadAsync(TenantContext database, string boundary, CancellationToken cancellationToken)
        {
            var gate = Volatile.Read(ref _shipping);
            if (gate is not null && gate.Database == database && gate.Boundary == boundary && gate.Entered.TrySetResult())
            {
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
        }

        private async ValueTask<T> ShippingReadAsync<T>(TenantContext database, string boundary, Func<ValueTask<T>> work)
        {
            var gate = Volatile.Read(ref _shipping);
            try
            {
                var result = await work();
                if (result is null && gate is not null && gate.Database == database && gate.Boundary == boundary && gate.Entered.Task.IsCompleted)
                {
                    _ = Interlocked.CompareExchange(ref _shipping, null, gate);
                    _ = gate.Refused.TrySetResult();
                }

                return result;
            }
            catch (SqliteException error)
            {
                Volatile.Write(ref _nativeConnection, Connections);
                _ = NativeReadError.TrySetResult((boundary, error));
                throw;
            }
            catch (ReplicaCopyFaultedException) when (gate is not null && gate.Database == database && gate.Boundary == boundary && gate.Entered.Task.IsCompleted)
            {
                _ = Interlocked.CompareExchange(ref _shipping, null, gate);
                _ = gate.Refused.TrySetResult();
                throw;
            }
        }

        public ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken)
        {
            if (database == ChangeGroups.System && refuseSystemCursor)
            {
                _ = SystemCursorRefused.TrySetResult();
                return ValueTask.FromResult<SourceCursor?>(null);
            }
            return inner.AckedAsync(database, peer, cancellationToken);
        }

        public ValueTask<bool> ProveAsync(TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken) =>
            inner.ProveAsync(database, incarnation, from, through, held, cancellationToken);

        public ValueTask<bool> AckAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken) =>
            BookkeepingAsync(database, stableReport: false, changeId, token => inner.AckAsync(database, incarnation, peer, changeId, stable, token), cancellationToken);

        public ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken) =>
            BookkeepingAsync(database, stableReport: true, 0, token => inner.RecordStableAsync(database, incarnation, peer, stable, token), cancellationToken);

        private async ValueTask<bool> BookkeepingAsync(TenantContext database, bool stableReport, long changeId, Func<CancellationToken, ValueTask<bool>> work, CancellationToken cancellationToken)
        {
            var gate = Volatile.Read(ref _held);
            if (gate is null || gate.Database != database || gate.StableReport != stableReport || (!stableReport && changeId <= gate.After)
                || Interlocked.CompareExchange(ref _held, null, gate) != gate)
            {
                return await work(cancellationToken);
            }

            _ = gate.Entered.TrySetResult(changeId);
            // Another stream worker may see the node fault first; keep the held guard observable under the test deadline.
            await gate.Release.Task.WaitAsync(gate.CancellationToken);
            if (gate.Busy)
            {
                Volatile.Write(ref _busy, gate);
                _ = gate.Refused.TrySetResult();
                throw new StorageBusyException();
            }
            try
            {
                var recorded = await work(gate.CancellationToken);
                if (!recorded)
                {
                    _ = gate.Refused.TrySetResult();
                }

                return recorded;
            }
            catch (ReplicaCopyFaultedException)
            {
                _ = gate.Refused.TrySetResult();
                throw;
            }
        }
    }
}
