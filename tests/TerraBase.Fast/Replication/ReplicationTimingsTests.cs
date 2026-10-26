using System.Text;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The replication lag instruments: one replicated write gives its origin a remote ack delay series for the peer region,
/// every step of its way from the ship pass to the receiver's written ack a duration, and the receiver's catalog lookups a
/// hit or miss count.
/// </summary>
public sealed class ReplicationTimingsTests
{
    private static readonly string[] SenderSteps = ["send.pass", "send.read", "send.ack"];
    private static readonly string[] ReceiverSteps = ["receive.loop", "receive.wait", "receive.open", "receive.begin", "receive.join", "receive.commit", "receive.stable", "receive.total"];

    [Test]
    public async Task A_replicated_write_records_its_remote_ack_delay_every_step_and_the_catalog_lookups()
    {
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();

        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_remote_ack_delay_seconds_count", value => value >= 1, "peer_region=\"sjc\"")).IsTrue();
        foreach (var step in SenderSteps)
        {
            await Assert.That(await gru.UntilMetricAsync("terrabase_replication_step_duration_seconds_count", value => value >= 1, $"step=\"{step}\"")).IsTrue().Because(step);
        }

        foreach (var step in ReceiverSteps)
        {
            await Assert.That(await sjc.UntilMetricAsync("terrabase_replication_step_duration_seconds_count", value => value >= 1, $"step=\"{step}\"")).IsTrue().Because(step);
        }

        await Assert.That(await sjc.UntilMetricAsync("terrabase_catalog_cache_lookups_total", value => value >= 1, "cache=\"registrations\"")).IsTrue();
    }
}
