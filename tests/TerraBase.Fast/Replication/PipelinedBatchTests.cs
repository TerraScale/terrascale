using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
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
/// Protocol 2's pipelined batches (docs/terrabase/replication/23-protocol-version-and-pipelining.md): the receiver applies a
/// database's batches in their chain only, so an ack never covers a batch that did not apply, and a protocol 1 sender keeps
/// one batch per database in flight.
/// </summary>
public sealed class PipelinedBatchTests
{
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

    private static PushFrame Batch(Features.Replication.V1.Database database, ulong after, ulong last, ReplicaId origin, string id)
    {
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
        OriginGroup[] groups = [new((long)last, [new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, JsonElement.Parse("""{"n":1}""")).DeltaAt(at.Stamp))])];
        return new PushFrame { Batch = new Batch { Database = database, After = after, LastChangeId = last, Groups = ChangeGroups.Encode(groups) } };
    }

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
        public async ValueTask<Hlc?> ApplyAsync(
            TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            await gate.WaitAsync(database, cancellationToken);
            return await inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
        }

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, incarnation, states, cancellationToken);

        public ValueTask<Ulid?> RecordFrontierAsync(
            TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
    }
}
