using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Clock;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Sync;

public sealed class HomePushTests
{
    [Test]
    public async Task A_local_write_in_sjc_is_visible_at_once_and_durability_home_answers_after_gru_applied_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats);
        await using var home = await world.HomeNodeAsync();

        var committed = await world.LocalAsync(homeDurable: true,
            ["INSERT INTO items (id, v) VALUES ('a', 'sjc-1')", "INSERT INTO items (id, v) VALUES ('b', 'sjc-1')", "UPDATE items SET v = 'sjc-2' WHERE id = 'a'"], token);

        await Assert.That(committed.Home!.State).IsEqualTo(WriteStates.Confirmed);
        await Assert.That((await world.HomeSqlAsync("SELECT id, v FROM items ORDER BY id")).GetRawText()).IsEqualTo("""[["a","sjc-2"],["b","sjc-1"]]""");
        await Assert.That(await world.RegionRowsAsync(token)).IsEqualTo("a=sjc-2,b=sjc-1");

        await world.HomeSqlAsync("INSERT INTO items (id, v) VALUES ('h', 'gru')");
        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);
        await Assert.That(await world.RegionRowsAsync(token)).IsEqualTo("a=sjc-2,b=sjc-1,h=gru");
        await Assert.That(world.Region.Overlay.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_cut_link_loses_nothing_a_pull_while_pending_applies_nothing_twice_and_redelivery_applies_once()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats);
        await world.HomeSqlAsync("CREATE TABLE counts (id TEXT PRIMARY KEY, n INTEGER NOT NULL)");
        await world.HomeSqlAsync("CREATE TABLE audit (id TEXT PRIMARY KEY, what TEXT)");
        await world.HomeSqlAsync("CREATE TRIGGER counts_audit AFTER INSERT ON counts BEGIN INSERT INTO audit (id, what) VALUES (new.id || '-audit', 'insert'); END");
        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);

        var pending = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            pending.Add((await world.LocalAsync(homeDurable: false, [$"INSERT INTO counts (id, n) VALUES ('c{i}', {i})"], token)).Id!);
        }

        for (var round = 0; round < 3; round++)
        {
            await world.HomeSqlAsync($"INSERT INTO items (id, v) VALUES ('home-{round}', 'gru')");
            await Assert.That((await world.Replicas.PullAsync(world.Database, fromHome: true, token))!.Changed).IsTrue();
            await Assert.That(await world.RegionScalarAsync("SELECT count(*) || '/' || sum(n) FROM counts", token)).IsEqualTo("5/10").Because($"pull {round} while the home is cut off");
            await Assert.That(await world.RegionScalarAsync("SELECT count(*) FROM audit", token)).IsEqualTo("5").Because($"pull {round}: re-applying fires no trigger");
        }

        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM counts")).GetRawText()).IsEqualTo("[[0]]");

        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
                (await world.HomeSqlAsync("SELECT count(*) || '/' || sum(n) FROM counts")).GetRawText() == """[["5/10"]]""", TimeSpan.FromSeconds(30));
            await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM audit")).GetRawText()).IsEqualTo("[[5]]");
        }

        await using (var restarted = await world.HomeNodeAsync())
        {
            var js = new NatsJSContext(world.Client);
            await Assert.That(await restarted.Push.AppliedAsync("sjc", world.Database, 5, token)).IsTrue();
            await Assert.That(await restarted.Push.ReplayFromAsync("sjc", world.Database, pending[0], token)).IsEqualTo(2UL);
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
                (await js.GetConsumerAsync(PushNames.Stream("sjc"), PushNames.Consumer("gru"), cancellation)).Info.NumAckPending == 0
                && (await js.GetConsumerAsync(PushNames.Stream("sjc"), PushNames.Consumer("gru"), cancellation)).Info.NumPending == 0, TimeSpan.FromSeconds(30));
            var again = await world.Region.PublishAgainAsync(world.Database, pending[0], token);
            await Assert.That(again.Duplicate).IsTrue();
        }

        await Assert.That((await world.HomeSqlAsync("SELECT count(*) || '/' || sum(n) FROM counts")).GetRawText()).IsEqualTo("""[["5/10"]]""");
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM audit")).GetRawText()).IsEqualTo("[[5]]");
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM terrascale.conflicts")).GetRawText()).IsEqualTo("[[0]]");

        await world.HomeSqlAsync("UPDATE counts SET n = 100 WHERE id = 'c0'");
        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);
        await Assert.That(await world.RegionScalarAsync("SELECT count(*) || '/' || sum(n) FROM counts", token)).IsEqualTo("5/110");
        await Assert.That(world.Region.Overlay.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_undecodable_push_is_parked_unapplied_and_a_later_write_still_applies()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats);
        await using var home = await world.HomeNodeAsync();
        var payload = "not-a-transaction"u8.ToArray();
        var js = new NatsJSContext(world.Client);
        var published = await js.PublishAsync(PushNames.Subject("sjc", "gru", world.Database), payload, cancellationToken: token);
        await Assert.That(published.Error).IsNull();
        await TerraBaseTestServer.EventuallyAsync(async cancellation =>
        {
            try
            {
                var consumer = await js.CreateOrderedConsumerAsync(PushNames.ParkStream("gru"), new NatsJSOrderedConsumerOpts
                {
                    FilterSubjects = [PushNames.ParkSubjects("gru")],
                    DeliverPolicy = NATS.Client.JetStream.Models.ConsumerConfigDeliverPolicy.All,
                }, cancellation);
                await foreach (var message in consumer.FetchNoWaitAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 4 }, cancellationToken: cancellation))
                {
                    if (message.Data is { } parked && parked.AsSpan().SequenceEqual(payload))
                    {
                        return true;
                    }
                }
            }
            catch (NATS.Client.JetStream.NatsJSApiException)
            {
                return false;
            }

            return false;
        }, TimeSpan.FromSeconds(8));
        var retained = await js.GetStreamAsync(PushNames.Stream("sjc"), cancellationToken: token);
        await Assert.That(retained.Info.State.Messages > 0).IsTrue();
        await Assert.That((await world.HomeSqlAsync("SELECT count(*) FROM items")).GetRawText()).IsEqualTo("[[0]]");
        var committed = await world.LocalAsync(true, ["INSERT INTO items (id, v) VALUES ('a', 'kept')"], token);
        await Assert.That(committed.Home!.State).IsEqualTo(WriteStates.Confirmed);
        await Assert.That((await world.HomeSqlAsync("SELECT id, v FROM items")).GetRawText()).IsEqualTo("""[["a","kept"]]""");
    }
}
