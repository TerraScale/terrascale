using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Replication;

public sealed class GcPresenceTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Listed_peer_without_progress_holds_only_databases_placed_in_its_region(bool placed)
    {
        var clock = new PeerStreamTests.ShiftedTime();
        await using var cluster = await StartAsync(clock);
        var machine = cluster["gru"];
        var member = machine.Api.Server.Services.GetRequiredService<LocalReplica>();
        var peer = new ReplicaId(member.Id.Value == 1 ? 2UL : 1UL);
        var store = await machine.StoreAsync();
        var placement = new DatabasePlacement("gru", placed ? ["sjc"] : [], 1);
        member.Presence.Listed([("unheard", "sjc")], member.Clock.Tick().Milliseconds);
        clock.Offset = TimeSpan.FromDays(2);
        var now = member.Clock.Tick();
        await AssertBoundsAsync(held: placed);

        member.Presence.Heard("unheard", peer, now.Milliseconds);
        await AssertBoundsAsync(held: placed);

        await using (var write = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await write.RecordAppliedAsync(peer, now, CancellationToken.None);
            await write.RecordStableAsync(peer, now, clock.GetUtcNow(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }

        await AssertBoundsAsync(held: false);
        member.Presence.Listed([], now.Milliseconds);
        await AssertBoundsAsync(held: false);

        async Task AssertBoundsAsync(bool held)
        {
            await using var read = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That((await read.GetStableAsync(now, CancellationToken.None, placement)).Value).IsEqualTo(held ? 0UL : now.Value);
            await Assert.That((await read.GetHorizonAsync(now, CancellationToken.None, placement)).Value > 0).IsEqualTo(!held);
        }
    }

    [Test]
    public async Task Listed_partitioned_and_unknown_peers_hold_gc_until_membership_has_been_empty_for_a_day()
    {
        var clock = new PeerStreamTests.ShiftedTime();
        await using var cluster = await StartAsync(clock);
        var machine = cluster["gru"];
        var member = machine.Api.Server.Services.GetRequiredService<LocalReplica>();
        var peer = new ReplicaId(member.Id.Value == 1 ? 2UL : 1UL);
        var store = await machine.StoreAsync();
        await using (var write = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await write.RecordStableAsync(peer, default, clock.GetUtcNow(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }

        member.Presence.Listed([], member.Clock.Tick().Milliseconds);
        await AssertHeldAsync();
        member.Presence.Listed([("partitioned", "sjc")], member.Clock.Tick().Milliseconds);
        member.Presence.Heard("partitioned", peer, member.Clock.Tick().Milliseconds);
        await AssertHeldAsync();
        clock.Offset = TimeSpan.FromDays(2);
        await AssertHeldAsync();

        var replacement = new ReplicaId(member.Id.Value == 3 ? 4UL : 3UL);
        var covered = member.Clock.Tick();
        member.Presence.Heard("partitioned", replacement, covered.Milliseconds);
        await using (var write = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await write.RecordAppliedAsync(replacement, covered, CancellationToken.None);
            await write.RecordStableAsync(replacement, covered, clock.GetUtcNow(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }

        await AssertHeldAsync();
        clock.Offset = TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1);
        await using (var read = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await read.GetStableAsync(member.Clock.Tick(), CancellationToken.None)).IsEqualTo(covered);
        }

        member.Presence.Listed([("unknown", "sjc")], member.Clock.Tick().Milliseconds);
        clock.Offset = TimeSpan.FromDays(4);
        await AssertHeldAsync();

        member.Presence.Listed([], member.Clock.Tick().Milliseconds);
        await AssertHeldAsync();
        clock.Offset = TimeSpan.FromDays(6);
        await using var read = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var now = member.Clock.Tick();
        await Assert.That(await read.GetStableAsync(now, CancellationToken.None)).IsEqualTo(now);
        await Assert.That((await read.GetHorizonAsync(now, CancellationToken.None)).Value).IsGreaterThan(0UL);

        async Task AssertHeldAsync()
        {
            await using var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None);
            var now = member.Clock.Tick();
            await Assert.That((await transaction.GetStableAsync(now, CancellationToken.None)).Value).IsEqualTo(0UL);
            await Assert.That((await transaction.GetHorizonAsync(now, CancellationToken.None)).Value).IsEqualTo(0UL);
        }
    }

    private static Task<PeerStreamTests.Cluster> StartAsync(PeerStreamTests.ShiftedTime clock) =>
        PeerStreamTests.Cluster.StartAsync(cdc: false, clock, configure: (_, services) =>
        {
            var streams = services.Single(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationFactory?.Method.ReturnType == typeof(PeerStreams));
            services.Remove(streams);
        }, "gru");
}
