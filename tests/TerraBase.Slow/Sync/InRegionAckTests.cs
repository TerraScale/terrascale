using TerraBase.Features.HomeApply;

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
    public async Task A_one_node_region_does_not_acknowledge_a_write_locally()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats, LocalPartner.Alone);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(world.Region.Started), TimeSpan.FromSeconds(10));
        await Assert.That(world.Region.LocalAckAvailable).IsFalse();
        await Assert.That(world.Region.TwoNodeAck).IsFalse();
        await using var home = await world.HomeNodeAsync();
        var committed = await world.LocalAsync(homeDurable: false, ["INSERT INTO items (id, v) VALUES ('a', 'sjc')"], token);

        await Assert.That(committed.Home).IsNotNull().Because("a region with no partner does not acknowledge the write on one disk");
        await Assert.That(committed.Home!.State).IsEqualTo(WriteStates.Confirmed);
    }
}
