using TerraBase.Features.HomeApply;
using TerraBase.Testing;

namespace TerraBase.Fast.Sync;

public sealed class InRegionAckTests
{
    [Test]
    public async Task A_local_write_is_acknowledged_once_sjc_2_holds_it_and_sjc_2_drops_it_after_gru_has_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats, LocalPartner.Node);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(world.Region.TwoNodeAck), TimeSpan.FromSeconds(10));

        var committed = await world.LocalAsync(homeDurable: false, ["INSERT INTO items (id, v) VALUES ('a', 'sjc')"], token);

        await Assert.That(committed.Home).IsNull().Because("the ack came from two sjc nodes, not from gru");
        await Assert.That(world.PartnerHolds).IsEqualTo(1);
        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async _ => (await world.HomeSqlAsync("SELECT v FROM items WHERE id = 'a'")).GetRawText() == """[["sjc"]]""",
                TimeSpan.FromSeconds(10));
            _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(world.PartnerHolds == 0), TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task A_one_node_region_acknowledges_locally_without_waiting_for_home_and_preserves_home_durability()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats, LocalPartner.Alone);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(world.Region.LocalAckAvailable), TimeSpan.FromSeconds(10));
        await Assert.That(world.Region.TwoNodeAck).IsFalse();
        var committed = await world.LocalAsync(homeDurable: false, ["INSERT INTO items (id, v) VALUES ('a', 'sjc')"], token);

        await Assert.That(committed.Home).IsNull().Because("a discovered one-node region uses a one-node quorum before N1 activation");
        await using var home = await world.HomeNodeAsync();
        var durable = await world.LocalAsync(homeDurable: true, ["INSERT INTO items (id, v) VALUES ('b', 'sjc')"], token);
        await Assert.That(durable.Home?.State).IsEqualTo(WriteStates.Confirmed);
    }
}
