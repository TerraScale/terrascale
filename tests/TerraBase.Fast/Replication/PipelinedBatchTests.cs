using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Protocol 2's pipelined batches (docs/terrabase/replication/23-protocol-version-and-pipelining.md): a sender keeps more
/// than one batch of a database in flight while the first waits for its remote apply, the receiver applies them in their
/// chain only, an ack never covers a batch that did not apply, a reconnect mid-window resends from the last ack, and a
/// protocol 1 peer on either side keeps one batch per database in flight.
/// </summary>
public sealed class PipelinedBatchTests
{
    [Test]
    public async Task Writes_behind_a_held_apply_ship_as_further_batches_and_none_is_acked_before_all_applied_in_order()
    {
        var gate = new ApplyGate();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, clock: null, (region, services) =>
        {
            if (region == "sjc")
            {
                services.AddSingleton<IChangeGroupSink>(provider => new GatedSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), gate));
            }
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var ids = await HoldThreeWritesAsync(cluster, gate);

        // All three groups went while the first waits; the ack stays where it was.
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Toward(gru, cluster.Database)?.DurableLag == 3));
        var held = Toward(gru, cluster.Database)!;
        await Assert.That(held.Acknowledged).IsLessThan(held.Head).Because("nothing after the held batch is acknowledged");
        await Assert.That(await sjc.ReadAsync(ids[2])).IsNull().Because("a later batch never applies before the one it follows");

        double delays = 0;
        _ = await gru.UntilMetricAsync("terrabase_replication_remote_ack_delay_seconds_count", value => (delays = value) >= 0, "peer_region=\"sjc\"");
        gate.Release();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            await Assert.That(await sjc.UntilAsync(id, $$"""{"n":{{index}}}""")).IsTrue();
        }

        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Toward(gru, cluster.Database) is { DurableLag: 0 } caught && caught.Acknowledged == caught.Head));
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_remote_ack_delay_seconds_count", value => value >= delays + 3, "peer_region=\"sjc\"")).IsTrue()
            .Because("every batch in flight keeps its own groups' stamps for the remote ack delay");
    }

    [Test]
    public async Task A_batch_refused_mid_window_refuses_the_ones_after_it_and_the_sender_resends_them_from_its_ack()
    {
        var gate = new ApplyGate();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, clock: null, (region, services) =>
        {
            if (region == "sjc")
            {
                services.AddSingleton<IChangeGroupSink>(provider => new GatedSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), gate));
            }
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var ids = await HoldThreeWritesAsync(cluster, gate);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Toward(gru, cluster.Database)?.DurableLag == 3));

        // The second batch fails to apply once: it is refused, the third (behind it) too, and both are sent again.
        gate.FailOnce(ids[1]);
        gate.Release();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            await Assert.That(await sjc.UntilAsync(id, $$"""{"n":{{index}}}""")).IsTrue();
        }

        await Assert.That(gate.Failed).IsTrue();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Toward(gru, cluster.Database) is { DurableLag: 0 } caught && caught.Acknowledged == caught.Head));
    }

    [Test]
    public async Task A_reconnect_mid_window_resends_from_the_last_ack_and_every_write_arrives()
    {
        var gate = new ApplyGate();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, clock: null, (region, services) =>
        {
            if (region == "sjc")
            {
                services.AddSingleton<IChangeGroupSink>(provider => new GatedSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), gate));
            }
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var ids = await HoldThreeWritesAsync(cluster, gate);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Toward(gru, cluster.Database)?.DurableLag == 3));

        // The receiver goes away with the window in flight and comes back without the gate.
        await sjc.StopAsync();
        await sjc.StartAsync();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            await Assert.That(await sjc.UntilAsync(id, $$"""{"n":{{index}}}""")).IsTrue();
        }

        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Toward(gru, cluster.Database) is { DurableLag: 0 } caught && caught.Acknowledged == caught.Head));
    }

    [Test]
    public async Task A_receiver_that_welcomes_without_a_version_gets_one_batch_per_database_at_a_time()
    {
        var gate = new ApplyGate();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, clock: null, (region, services) =>
        {
            if (region == "sjc")
            {
                services.AddSingleton<IChangeGroupSink>(provider => new GatedSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), gate));
                services.AddGrpc(options => options.Interceptors.Add<UnversionedWelcome>());
            }
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var ids = await HoldThreeWritesAsync(cluster, gate);

        // Two ship passes after the last write still keep only the held batch in flight.
        double passes = 0;
        _ = await gru.UntilMetricAsync("terrabase_replication_step_duration_seconds_count", value => (passes = value) >= 0, "step=\"send.pass\"");
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_step_duration_seconds_count", value => value >= passes + 2, "step=\"send.pass\"")).IsTrue();
        await Assert.That(Toward(gru, cluster.Database)?.DurableLag).IsEqualTo(1L);

        gate.Release();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            await Assert.That(await sjc.UntilAsync(id, $$"""{"n":{{index}}}""")).IsTrue();
        }
    }

    [Test]
    public async Task The_receiver_applies_a_chain_only_and_a_batch_after_a_refused_one_is_refused_until_the_chain_restarts()
    {
        await using var api = await ManagementApi.StartAsync();
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: deadline.Token);
        var origin = ReplicaId.Of(1, 11, 0);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc", Protocol = 2 } });
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome.Protocol).IsEqualTo(ReplicationProtocol.Version);
        var sender = ChangeGroups.Wire(tenant, Ulid.NewUlid());
        var docs = Enumerable.Range(0, 4).Select(_ => EntityIds.New().ToString()).ToArray();

        // 1 applies; 2 cannot be decoded and is refused; 3 follows it and is refused too, though it is valid.
        await call.RequestStream.WriteAsync(Batch(sender, after: 0, last: 1, origin, docs[0]));
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = sender, After = 1, LastChangeId = 2, Groups = ByteString.CopyFromUtf8("garbage") } });
        await call.RequestStream.WriteAsync(Batch(sender, after: 2, last: 3, origin, docs[2]));
        await Assert.That(string.Join(", ", await AnswersAsync(call, 3, deadline.Token))).IsEqualTo("(1, False), (0, True), (0, True)");

        // A batch that skips what was refused is refused; one following the last applied batch restarts the chain.
        await call.RequestStream.WriteAsync(Batch(sender, after: 3, last: 4, origin, docs[3]));
        await Assert.That(string.Join(", ", await AnswersAsync(call, 1, deadline.Token))).IsEqualTo("(0, True)");
        await call.RequestStream.WriteAsync(Batch(sender, after: 1, last: 2, origin, docs[1]));
        await call.RequestStream.WriteAsync(Batch(sender, after: 2, last: 3, origin, docs[2]));
        await Assert.That(string.Join(", ", await AnswersAsync(call, 2, deadline.Token))).IsEqualTo("(2, False), (3, False)");

        var store = await engine.OpenDatabaseAsync(tenant, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.GetAsync("items", docs[0], CancellationToken.None)).IsNotNull();
        await Assert.That(await read.GetAsync("items", docs[1], CancellationToken.None)).IsNotNull();
        await Assert.That(await read.GetAsync("items", docs[2], CancellationToken.None)).IsNotNull();
        await Assert.That(await read.GetAsync("items", docs[3], CancellationToken.None)).IsNull().Because("its batch skipped a refused one");
    }

    [Test]
    public async Task A_refusal_is_answered_after_its_database_batch_in_progress_and_a_new_copy_restarts_only_from_its_resume_position()
    {
        var gate = new ApplyGate();
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IChangeGroupSink>(provider => new GatedSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), gate)));
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: deadline.Token);
        var origin = ReplicaId.Of(1, 13, 0);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc", Protocol = 2 } });
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        var (first, second) = (ChangeGroups.Wire(tenant, Ulid.NewUlid()), ChangeGroups.Wire(tenant, Ulid.NewUlid()));
        var docs = Enumerable.Range(0, 3).Select(_ => EntityIds.New().ToString()).ToArray();

        // A batch of another copy arrives while the first copy's batch applies: refused, and answered after it.
        gate.Hold(tenant);
        await call.RequestStream.WriteAsync(Batch(first, after: 0, last: 1, origin, docs[0]));
        await gate.Entered.Task.WaitAsync(deadline.Token);
        await call.RequestStream.WriteAsync(Batch(second, after: 0, last: 1, origin, docs[1]));
        gate.Release();
        await Assert.That(string.Join(", ", await AnswersAsync(call, 2, deadline.Token))).IsEqualTo("(1, False), (0, True)");

        // The new copy's next batch would apply past the refused one: refused until a batch from its resume position.
        await call.RequestStream.WriteAsync(Batch(second, after: 1, last: 2, origin, docs[2]));
        await Assert.That(string.Join(", ", await AnswersAsync(call, 1, deadline.Token))).IsEqualTo("(0, True)");
        await call.RequestStream.WriteAsync(Batch(second, after: 0, last: 1, origin, docs[1]));
        await call.RequestStream.WriteAsync(Batch(second, after: 1, last: 2, origin, docs[2]));
        await Assert.That(string.Join(", ", await AnswersAsync(call, 2, deadline.Token))).IsEqualTo("(1, False), (2, False)");

        var store = await engine.OpenDatabaseAsync(tenant, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        foreach (var doc in docs)
        {
            await Assert.That(await read.GetAsync("items", doc, CancellationToken.None)).IsNotNull();
        }
    }

    [Test]
    public async Task A_sender_without_a_version_still_gets_a_second_batch_of_a_busy_database_refused()
    {
        var gate = new ApplyGate();
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IChangeGroupSink>(provider => new GatedSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), gate)));
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: deadline.Token);
        var origin = ReplicaId.Of(1, 12, 0);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        var sender = ChangeGroups.Wire(tenant, Ulid.NewUlid());
        gate.Hold(tenant);
        await call.RequestStream.WriteAsync(Batch(sender, after: 0, last: 1, origin, EntityIds.New().ToString()));
        await gate.Entered.Task.WaitAsync(deadline.Token);
        await call.RequestStream.WriteAsync(Batch(sender, after: 1, last: 2, origin, EntityIds.New().ToString()));
        await Assert.That(string.Join(", ", await AnswersAsync(call, 1, deadline.Token))).IsEqualTo("(0, True)")
            .Because("the second batch of a database in progress is refused at once");
        gate.Release();
        await Assert.That(string.Join(", ", await AnswersAsync(call, 1, deadline.Token))).IsEqualTo("(1, False)");
    }

    [Test]
    public async Task Batches_queued_behind_an_apply_join_one_transaction_and_each_gets_its_own_ack_in_order()
    {
        var gate = new ApplyGate();
        await using var receiver = await Receiver.StartAsync(gate);
        var (call, sender, docs) = (receiver.Call, receiver.Sender, receiver.Docs);
        var stamps = Enumerable.Range(0, 4).Select(index => new Hlc(((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16) + (ulong)index)).ToArray();
        await receiver.QueueBehindHeldAsync(Enumerable.Range(1, 3).Select(index => Batch(sender, after: (ulong)index, last: (ulong)index + 1, receiver.Origin, docs[index], stamps[index])));
        gate.Release();

        var acks = await AcksAsync(call, 4, receiver.Deadline);
        await Assert.That(string.Join(", ", acks.Select(ack => (ack.ChangeId, ack.Refused)))).IsEqualTo("(1, False), (2, False), (3, False), (4, False)");
        await Assert.That(string.Join(", ", acks.Skip(1).Select(ack => ack.Applied))).IsEqualTo(string.Join(", ", stamps[1..].Select(stamp => stamp.Value)))
            .Because("each ack names its own batch's highest stamp");
        await Assert.That(acks.Skip(1).Select(ack => ack.Stable).Distinct().Count()).IsEqualTo(1).Because("one stable read after their one commit");
        await Assert.That(string.Join(", ", gate.Calls(receiver.Tenant).Select(applied => applied.Groups))).IsEqualTo("1, 3")
            .Because("the three batches queued behind the held one apply in one transaction");
        await receiver.AssertStoredAsync(docs[..4], stored: true);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_chunk_between_coalesced_inline_batches_preserves_the_chain_and_refuses_followers_when_unread(bool readable)
    {
        var gate = new ApplyGate();
        var bucket = new MemoryObjectStore();
        await using var receiver = await Receiver.StartAsync(gate, services => services.AddSingleton<IObjectStore>(bucket.For("gru")));
        var docs = Enumerable.Range(0, 5).Select(_ => EntityIds.New().ToString()).ToArray();
        var frames = docs.Select((id, index) => Batch(receiver.Sender, (ulong)index + 1, (ulong)index + 2, receiver.Origin, id)).ToArray();
        var envelope = frames[2].Batch.Groups;
        var key = LogChunks.Prefix + "sjc-machine/" + Ulid.NewUlid();
        var (sealedBytes, seal) = LogChunks.Seal(key, envelope.Span);
        frames[2].Batch.Chunk = new LogChunk
        {
            Key = key,
            Seal = ByteString.CopyFrom(seal),
            Length = (uint)sealedBytes.Length,
            Decoded = (uint)ApplicationEnvelope.DecodedLength(envelope.Span),
        };
        frames[2].Batch.Groups = ByteString.Empty;
        if (readable)
        {
            bucket.Objects[key] = sealedBytes;
        }

        await receiver.QueueBehindHeldAsync(frames);
        gate.Release();
        var acks = await AcksAsync(receiver.Call, 6, receiver.Deadline);
        await Assert.That(string.Join(", ", acks.Select(ack => (ack.ChangeId, ack.Refused)))).IsEqualTo(readable
            ? "(1, False), (2, False), (3, False), (4, False), (5, False), (6, False)"
            : "(1, False), (2, False), (3, False), (0, True), (0, True), (0, True)");
        await Assert.That(acks[3].ChunkUnread).IsEqualTo(!readable);
        await Assert.That(string.Join(", ", gate.Calls(receiver.Tenant).Select(applied => applied.Groups))).IsEqualTo(readable ? "1, 2, 1, 2" : "1, 2")
            .Because("inline neighbors coalesce on each side; the chunk applies on its own turn");
        await receiver.AssertStoredAsync(docs[..2], stored: true);
        await receiver.AssertStoredAsync(docs[2..], stored: readable);
    }

    [Test]
    public async Task A_database_whose_batch_applied_on_the_stream_does_not_wait_for_a_catalog_frame_in_progress()
    {
        var gate = new ApplyGate();
        await using var receiver = await Receiver.StartAsync(gate);
        var (call, sender, docs) = (receiver.Call, receiver.Sender, receiver.Docs);
        await call.RequestStream.WriteAsync(Batch(sender, after: 0, last: 1, receiver.Origin, docs[0]));
        await Assert.That(string.Join(", ", (await AcksAsync(call, 1, receiver.Deadline)).Select(ack => (ack.ChangeId, ack.Refused)))).IsEqualTo("(1, False)");

        // A catalog frame whose apply is held: before TS-974, every later client batch on the stream waited for it.
        gate.Hold(ChangeGroups.System);
        await call.RequestStream.WriteAsync(Batch(ChangeGroups.Wire(ChangeGroups.System, Ulid.NewUlid()), after: 0, last: 1, receiver.Origin, EntityIds.New().ToString()));
        await gate.Entered.Task.WaitAsync(receiver.Deadline);
        await call.RequestStream.WriteAsync(Batch(sender, after: 1, last: 2, receiver.Origin, docs[1]));
        await Assert.That(string.Join(", ", (await AcksAsync(call, 1, receiver.Deadline)).Select(ack => (ack.ChangeId, ack.Refused)))).IsEqualTo("(2, False)")
            .Because("the database's registration committed before its first batch applied");
        await receiver.AssertStoredAsync(docs[..2], stored: true);
        gate.Release();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_refusal_or_fault_of_a_joined_transaction_acknowledges_none_of_its_batches(bool afterCommit)
    {
        var gate = new ApplyGate();
        var commits = 0;
        Func<Task>? fault = null;
        Task? recording = null;
        await using var receiver = await Receiver.StartAsync(gate, services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            // The joined transaction committed: a fault of the copy (refused in memory at once) then refuses its stable read.
            if (step == "replication.apply.committed" && Volatile.Read(ref fault) is { } armed && Interlocked.Increment(ref commits) == 2)
            {
                recording = armed();
            }
        }));
        var (call, sender, docs) = (receiver.Call, receiver.Sender, receiver.Docs);
        var faults = receiver.Api.Server.Services.GetRequiredService<IReplicaCopyFaults>();
        // Opened before any fault; BEGIN DEFERRED takes no read snapshot yet.
        await using var forensic = await (await receiver.StoreAsync()).BeginTransactionAsync(write: false, CancellationToken.None);
        await receiver.QueueBehindHeldAsync(Enumerable.Range(1, 3).Select(index => Batch(sender, after: (ulong)index, last: (ulong)index + 1, receiver.Origin, docs[index])));
        if (afterCommit)
        {
            var incarnation = (await receiver.StoreAsync()).Incarnation;
            Volatile.Write(ref fault, () => faults.RecordAsync(receiver.Tenant, incarnation, "a test fault after the joined commit", CancellationToken.None).AsTask());
        }
        else
        {
            gate.FailOnce(docs[2]);
        }

        gate.Release();
        await Assert.That(string.Join(", ", await AnswersAsync(call, 4, receiver.Deadline))).IsEqualTo("(1, False), (0, True), (0, True), (0, True)")
            .Because("the batches behind the refused first one of the transaction are refused with it");
        await Assert.That(string.Join(", ", gate.Calls(receiver.Tenant).Select(applied => applied.Groups))).IsEqualTo("1, 3");
        foreach (var doc in docs[1..4])
        {
            await Assert.That(await forensic.GetAsync("items", doc, CancellationToken.None) is not null).IsEqualTo(afterCommit)
                .Because("the faulted transaction committed, but none of its batches is acknowledged");
        }

        if (afterCommit)
        {
            await Assert.That(recording).IsNotNull();
            await recording!.WaitAsync(receiver.Deadline);
        }
        else
        {
            // The chain restarts from the last ack.
            foreach (var index in new[] { 1, 2, 3 })
            {
                await call.RequestStream.WriteAsync(Batch(sender, after: (ulong)index, last: (ulong)index + 1, receiver.Origin, docs[index]));
            }

            await Assert.That(string.Join(", ", await AnswersAsync(call, 3, receiver.Deadline))).IsEqualTo("(2, False), (3, False), (4, False)");
            await receiver.AssertStoredAsync(docs[1..4], stored: true);
        }
    }

    [Test]
    [Arguments("groups")]
    [Arguments("deltas")]
    [Arguments("bytes")]
    public async Task A_joined_transaction_holds_no_more_than_one_batch_of_groups_and_bytes_and_one_transaction_of_deltas(string bound)
    {
        var gate = new ApplyGate();
        await using var receiver = await Receiver.StartAsync(gate);
        var (call, sender) = (receiver.Call, receiver.Sender);
        // Three batches of 400 groups, of one 400-delta group, or of one 750 KB document: two fit in one transaction, three do not.
        var payload = JsonElement.Parse($$"""{"blob":"{{new string('x', bound == "bytes" ? 750_000 : 8)}}"}""");
        var change = 1L;
        await receiver.QueueBehindHeldAsync(Enumerable.Range(1, 3).Select(index =>
        {
            var after = (ulong)change;
            OriginGroup[] groups = bound switch
            {
                "groups" => [.. Enumerable.Range(0, 400).Select(_ => Group(++change, 1, receiver.Origin, payload))],
                "deltas" => [Group(++change, 400, receiver.Origin, payload)],
                _ => [Group(++change, 1, receiver.Origin, payload)],
            };
            return new PushFrame { Batch = new Batch { Database = sender, After = after, LastChangeId = (ulong)change, Groups = ChangeGroups.Encode(groups) } };
        }));
        gate.Release();

        var step = bound == "groups" ? 400 : 1;
        await Assert.That(string.Join(", ", await AnswersAsync(call, 4, receiver.Deadline))).IsEqualTo($"(1, False), ({change - (2 * step)}, False), ({change - step}, False), ({change}, False)");
        var calls = gate.Calls(receiver.Tenant);
        await Assert.That(string.Join(", ", calls.Select(applied => bound == "deltas" ? applied.Deltas : applied.Groups))).IsEqualTo(bound switch
        {
            "groups" => "1, 800, 400",
            "deltas" => "1, 800, 400",
            _ => "1, 2, 1",
        }).Because("the first batch that would cross the bound applies on its own turn");
    }

    // Holds sjc's applies of the cluster's database, then writes three documents in three commits on gru: the first batch
    // waits in the receiver's apply. The collection is created and replicated first.
    private static async Task<string[]> HoldThreeWritesAsync(PeerStreamTests.Cluster cluster, ApplyGate gate)
    {
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var ready = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", ready, Json("""{"ready":true}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(ready, """{"ready":true}""")).IsTrue();

        gate.Hold(cluster.Database);
        string[] ids = [.. Enumerable.Range(0, 3).Select(_ => EntityIds.New().ToString())];
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", ids[0], Json("""{"n":0}"""), CancellationToken.None));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        foreach (var index in new[] { 1, 2 })
        {
            await gru.WriteAsync(async transaction => await transaction.PutAsync("items", ids[index], Json($$"""{"n":{{index}}}"""), CancellationToken.None));
        }

        return ids;
    }

    private static ReplicationDatabaseStatus? Toward(PeerStreamTests.Machine machine, TenantContext database) =>
        machine.Api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers.Where(peer => peer.Region == "sjc")
            .SelectMany(peer => peer.Databases).FirstOrDefault(status => status.DatabaseId == database.DatabaseId);

    private static PushFrame Batch(Features.Replication.V1.Database database, ulong after, ulong last, ReplicaId origin, string id, Hlc? stamp = null)
    {
        var at = new EntryVersion(stamp ?? new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
        OriginGroup[] groups = [new((long)last, [new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, JsonElement.Parse("""{"n":1}""")).DeltaAt(at.Stamp))])];
        return new PushFrame { Batch = new Batch { Database = database, After = after, LastChangeId = last, Groups = ChangeGroups.Encode(groups) } };
    }

    // A group of `deltas` new documents, each with the payload.
    private static OriginGroup Group(long changeId, int deltas, ReplicaId origin, JsonElement payload)
    {
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
        return new(changeId, [.. Enumerable.Range(0, deltas).Select(_ => new ChangeDelta("items", EntityIds.New().ToString(), at.Stamp, DocumentState.Bottom.Put(at, payload).DeltaAt(at.Stamp)))]);
    }

    private static async Task<List<Ack>> AcksAsync(AsyncDuplexStreamingCall<PushFrame, AckFrame> call, int count, CancellationToken cancellationToken)
    {
        List<Ack> acks = [];
        while (acks.Count < count && await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (call.ResponseStream.Current.Ack is { } ack)
            {
                acks.Add(ack);
            }
        }

        return acks;
    }

    // One node with a gated sink and a protocol 2 push stream into one of its databases, from a test origin.
    private sealed class Receiver : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        private readonly Grpc.Net.Client.GrpcChannel _channel;

        private Receiver(ManagementApi api, TenantContext tenant, ApplyGate gate)
        {
            (Api, Tenant, Gate, Sender) = (api, tenant, gate, ChangeGroups.Wire(tenant, Ulid.NewUlid()));
            _channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
            Call = new Features.Replication.V1.Replication.ReplicationClient(_channel).Push(cancellationToken: Deadline);
        }

        public ManagementApi Api { get; }

        public TenantContext Tenant { get; }

        public ApplyGate Gate { get; }

        public ReplicaId Origin { get; } = ReplicaId.Of(1, 14, 0);

        public Features.Replication.V1.Database Sender { get; }

        public AsyncDuplexStreamingCall<PushFrame, AckFrame> Call { get; }

        public string[] Docs { get; } = [.. Enumerable.Range(0, 4).Select(_ => EntityIds.New().ToString())];

        public CancellationToken Deadline => _deadline.Token;

        public static async Task<Receiver> StartAsync(ApplyGate gate, Action<IServiceCollection>? services = null)
        {
            var api = await ManagementApi.StartAsync(services: collection =>
            {
                services?.Invoke(collection);
                collection.AddSingleton<IChangeGroupSink>(provider => new GatedSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), gate));
            });
            var managed = await api.CreateManagedDatabaseAsync();
            using (var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read"))
            {
                await ManagementApi.CreateCollectionAsync(data, managed, "items");
            }

            var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
            var receiver = new Receiver(api, tenant, gate);
            await receiver.Call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = receiver.Origin.Value, Region = "sjc", Protocol = 2 } });
            await Assert.That(await receiver.Call.ResponseStream.MoveNext(receiver.Deadline)).IsTrue();
            return receiver;
        }

        // Holds the first batch's apply, then sends the batches after it and waits until the stream admitted them: the
        // heartbeat behind them is answered once the frames before it were read.
        public async Task QueueBehindHeldAsync(IEnumerable<PushFrame> batches)
        {
            Gate.Hold(Tenant);
            await Call.RequestStream.WriteAsync(Batch(Sender, after: 0, last: 1, Origin, Docs[0]));
            await Gate.Entered.Task.WaitAsync(Deadline);
            foreach (var batch in batches)
            {
                await Call.RequestStream.WriteAsync(batch);
            }

            await Call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() });
            do
            {
                await Assert.That(await Call.ResponseStream.MoveNext(Deadline)).IsTrue();
            }
            while (Call.ResponseStream.Current.KindCase != AckFrame.KindOneofCase.None);
        }

        public async Task<IDatabaseStore> StoreAsync() =>
            await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(Tenant, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");

        public async Task AssertStoredAsync(IEnumerable<string> docs, bool stored)
        {
            await using var read = await (await StoreAsync()).BeginTransactionAsync(write: false, CancellationToken.None);
            foreach (var doc in docs)
            {
                await Assert.That(await read.GetAsync("items", doc, CancellationToken.None) is not null).IsEqualTo(stored);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Call.Dispose();
            _channel.Dispose();
            _deadline.Dispose();
            await Api.DisposeAsync();
        }
    }

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    // The next `count` acks in arrival order: change id (0 for a refusal) and whether each was refused.
    private static async Task<List<(ulong ChangeId, bool Refused)>> AnswersAsync(AsyncDuplexStreamingCall<PushFrame, AckFrame> call, int count, CancellationToken cancellationToken)
    {
        List<(ulong ChangeId, bool Refused)> answers = [];
        while (answers.Count < count && await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (call.ResponseStream.Current.Ack is { } ack)
            {
                answers.Add((ack.ChangeId, ack.Refused));
            }
        }

        return answers;
    }

    private sealed class ApplyGate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TenantContext? _held;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold(TenantContext database) => Volatile.Write(ref _held, database with { Plan = PlanTier.Hobby });

        private string? _failing;

        public bool Failed { get; private set; }

        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Database, int Groups, int Deltas)> _calls = new();

        // The applies of the database, in order: groups and deltas each joined.
        public List<(int Groups, int Deltas)> Calls(TenantContext database) =>
            [.. _calls.Where(call => call.Database == database.DatabaseId).Select(call => (call.Groups, call.Deltas))];

        public void Record(TenantContext database, IReadOnlyList<OriginGroup> groups) =>
            _calls.Enqueue((database.DatabaseId ?? "", groups.Count, groups.Sum(group => group.Deltas.Count)));

        public void Release() => _release.TrySetResult();

        // The next apply holding this document fails, once.
        public void FailOnce(string id) => Volatile.Write(ref _failing, id);

        public void ThrowIfFailing(IReadOnlyList<OriginGroup> groups)
        {
            if (Volatile.Read(ref _failing) is { } id && groups.Any(group => group.Deltas.Any(delta => delta.Id == id))
                && Interlocked.CompareExchange(ref _failing, null, id) == id)
            {
                Failed = true;
                throw new IOException("the apply failed");
            }
        }

        public async Task WaitAsync(TenantContext database, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _held) is { } held && held == database with { Plan = PlanTier.Hobby })
            {
                _ = Entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class GatedSink(IChangeGroupSink inner, ApplyGate gate) : IChangeGroupSink
    {
        public async ValueTask<Hlc?> ApplyAsync(
            TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            await gate.WaitAsync(database, cancellationToken);
            gate.Record(database, groups);
            gate.ThrowIfFailing(groups);
            return await inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
        }

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, incarnation, states, cancellationToken);

        public ValueTask<Ulid?> RecordFrontierAsync(
            TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
    }

    // An old receiver: it never reads Hello.protocol, and its Welcome names none.
    private sealed class UnversionedWelcome : Interceptor
    {
        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
            continuation(new Reader<TRequest>(requestStream), new Writer<TResponse>(responseStream), context);

        private sealed class Reader<T>(IAsyncStreamReader<T> inner) : IAsyncStreamReader<T>
        {
            public T Current => inner.Current;

            public async Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                var more = await inner.MoveNext(cancellationToken);
                if (more && inner.Current is PushFrame { Hello: { } hello })
                {
                    hello.Protocol = 0;
                }

                return more;
            }
        }

        private sealed class Writer<T>(IServerStreamWriter<T> inner) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }

            public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

            public Task WriteAsync(T message, CancellationToken cancellationToken)
            {
                if (message is AckFrame { Welcome: { } welcome })
                {
                    welcome.Protocol = 0;
                }

                return inner.WriteAsync(message, cancellationToken);
            }
        }
    }
}
