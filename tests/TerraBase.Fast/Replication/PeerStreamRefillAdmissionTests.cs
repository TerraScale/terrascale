using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class PeerStreamRefillAdmissionTests
{
    [Test]
    public async Task Refills_waiting_for_the_catalog_reserve_admission_before_the_catalog_commits()
    {
        var origin = ReplicaId.Of(1, 7, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            services.AddSingleton<IChangeGroupSink>(provider => new HeldCatalogSink(
                ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), origin, entered, release));
            // Progress deadlines have their own coverage; catalog admission stays gated until this test releases it.
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreamReceiver>(
                provider, new FakeTimeProvider(DateTimeOffset.UtcNow)));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: deadline.Token);
        try
        {
            await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
            await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();

            var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
            var delta = new ChangeDelta("receiver_admission", Ulid.NewUlid().ToString(), at.Stamp,
                DocumentState.Bottom.Put(at, JsonElement.Parse("""{"n":1}""")).DeltaAt(at.Stamp));
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch
                {
                    Database = ChangeGroups.Wire(ChangeGroups.System, Ulid.NewUlid()),
                    LastChangeId = 1,
                    Groups = ChangeGroups.Encode([new OriginGroup(1, [delta])]),
                },
            });
            await entered.Task.WaitAsync(deadline.Token);

            Features.Replication.V1.Database? fifth = null;
            for (var index = 0; index < 5; index++)
            {
                var database = new TenantContext(Ulid.NewUlid().ToString(), PlanTier.Hobby,
                    Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString());
                var wire = ChangeGroups.Wire(database, Ulid.NewUlid());
                await call.RequestStream.WriteAsync(new PushFrame
                {
                    Refill = new Refill { Database = wire, ChangeId = 1 },
                });
                fifth = wire;
            }

            await call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() });
            await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
            var refusal = call.ResponseStream.Current.Ack;
            // The heartbeat proves all five refills were read. It must follow the fifth refill's immediate refusal.
            await Assert.That(refusal).IsNotNull().Because("the fifth refill must be refused before the catalog gate releases");
            await Assert.That(refusal!.Refused).IsTrue();
            await Assert.That(refusal.Database).IsEqualTo(fifth);
            await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.KindCase).IsEqualTo(AckFrame.KindOneofCase.None);
        }
        finally
        {
            // Cancel before releasing so the synthetic system delta never needs to commit during cleanup.
            call.Dispose();
            _ = release.TrySetResult();
        }
    }

    private sealed class HeldCatalogSink(
        IChangeGroupSink sink, ReplicaId chosenOrigin, TaskCompletionSource entered, TaskCompletionSource release) : IChangeGroupSink
    {
        public async ValueTask<Hlc?> ApplyAsync(
            TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            if (database == ChangeGroups.System && origin == chosenOrigin)
            {
                _ = entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await sink.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
        }

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            sink.RepairAsync(database, incarnation, states, cancellationToken);

        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            sink.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
    }
}
