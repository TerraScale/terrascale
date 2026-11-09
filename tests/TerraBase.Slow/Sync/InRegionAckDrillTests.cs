using TerraBase.Fast.Sync;

namespace TerraBase.Slow.Sync;

public sealed class InRegionAckDrillTests
{
    [Test]
    public async Task Killing_the_acking_sjc_node_right_after_an_ack_loses_no_acknowledged_write()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var nats = await JetStreamServer.StartAsync(token);
        var stopped = false;
        try
        {
            await using var world = await LocalWriteWorld.StartAsync(nats, LocalPartner.Node);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(world.Region.TwoNodeAck), TimeSpan.FromSeconds(10));
            for (var i = 0; i < 3; i++)
            {
                var committed = await world.LocalAsync(homeDurable: false, [$"INSERT INTO items (id, v) VALUES ('k{i}', 'sjc')"], token);
                await Assert.That(committed.Home).IsNull();
            }

            await world.KillRegionNodeAsync();
            await nats.DisposeAsync();
            stopped = true;

            await TerraBaseTestServer.EventuallyAsync(async _ => (await world.HomeSqlAsync("SELECT count(*) FROM items")).GetRawText() == "[[3]]",
                TimeSpan.FromSeconds(60));
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(world.PartnerHolds == 0), TimeSpan.FromSeconds(10));
            await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM terrascale.conflicts")).GetRawText()).IsEqualTo("[[0]]");
        }
        finally
        {
            if (!stopped)
            {
                await nats.DisposeAsync();
            }
        }
    }
}
