using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
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

        gate.Release();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            await Assert.That(await sjc.UntilAsync(id, $$"""{"n":{{index}}}""")).IsTrue();
        }

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

    private static PushFrame Batch(Features.Replication.V1.Database database, ulong after, ulong last, ReplicaId origin, string id)
    {
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
        OriginGroup[] groups = [new((long)last, [new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, JsonElement.Parse("""{"n":1}""")).DeltaAt(at.Stamp))])];
        return new PushFrame { Batch = new Batch { Database = database, After = after, LastChangeId = last, Groups = ChangeGroups.Encode(groups) } };
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

        public void Release() => _release.TrySetResult();

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
        public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(database, cancellationToken);
            return await inner.ApplyAsync(database, origin, groups, cancellationToken);
        }

        public ValueTask<int?> RepairAsync(TenantContext database, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, states, cancellationToken);

        public ValueTask RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, CancellationToken cancellationToken) =>
            inner.RecordFrontierAsync(database, origin, frontier, cancellationToken);
    }

    // An old receiver's Welcome: no protocol.
    private sealed class UnversionedWelcome : Interceptor
    {
        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
            continuation(requestStream, new Writer<TResponse>(responseStream), context);

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
