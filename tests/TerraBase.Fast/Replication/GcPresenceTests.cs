using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;

namespace TerraBase.Fast.Replication;

public sealed class GcPresenceTests
{
    [Test]
    public async Task Unknown_listed_peer_without_a_progress_row_holds_gc_until_its_identity_is_known()
    {
        var clock = new PeerStreamTests.ShiftedTime();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: false, clock, "gru");
        var machine = cluster["gru"];
        await machine.Api.Server.Services.GetRequiredService<PeerStreams>().StopAsync(CancellationToken.None);
        var member = machine.Api.Server.Services.GetRequiredService<LocalReplica>();
        var peer = new ReplicaId(member.Id.Value == 1 ? 2UL : 1UL);
        var store = await machine.StoreAsync();
        member.Presence.Listed(["unheard"], member.Clock.Tick().Milliseconds);
        clock.Offset = TimeSpan.FromDays(2);
        await using var read = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var now = member.Clock.Tick();
        await Assert.That((await read.GetStableAsync(now, CancellationToken.None)).Value).IsEqualTo(0UL);
        await Assert.That((await read.GetHorizonAsync(now, CancellationToken.None)).Value).IsEqualTo(0UL);

        member.Presence.Heard("unheard", peer);
        await Assert.That(await read.GetStableAsync(now, CancellationToken.None)).IsEqualTo(now);
        await Assert.That((await read.GetHorizonAsync(now, CancellationToken.None)).Value).IsGreaterThan(0UL);

        member.Presence.Listed([], now.Milliseconds);
        await Assert.That(await read.GetStableAsync(now, CancellationToken.None)).IsEqualTo(now);
        await Assert.That((await read.GetHorizonAsync(now, CancellationToken.None)).Value).IsGreaterThan(0UL);
    }

    [Test]
    public async Task Listed_partitioned_and_unknown_peers_hold_gc_until_membership_has_been_empty_for_a_day()
    {
        var clock = new PeerStreamTests.ShiftedTime();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: false, clock, "gru");
        var machine = cluster["gru"];
        await machine.Api.Server.Services.GetRequiredService<PeerStreams>().StopAsync(CancellationToken.None);
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
        member.Presence.Listed(["partitioned"], member.Clock.Tick().Milliseconds);
        member.Presence.Heard("partitioned", peer);
        await AssertHeldAsync();
        clock.Offset = TimeSpan.FromDays(2);
        await AssertHeldAsync();

        member.Presence.Listed(["unknown"], member.Clock.Tick().Milliseconds);
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
}
