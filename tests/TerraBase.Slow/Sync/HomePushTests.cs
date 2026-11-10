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
using TerraBase.Features.LocalAck;
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

    [Test]
    public async Task A_stuck_database_backoff_does_not_hold_the_parallel_apply_slot()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await LocalWriteWorld.StartAsync(nats);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = 0;
        var apply = new CountingApply((database, transaction) =>
        {
            if (database.DatabaseId == world.Database.DatabaseId)
            {
                ready.TrySetResult();
                return Confirmed(transaction);
            }

            if (Interlocked.Increment(ref busy) == 16)
            {
                filled.TrySetResult();
            }

            return ready.Task.IsCompleted ? Confirmed(transaction) : null;
        });
        var options = world.Api.Server.Services.GetRequiredService<TerraBaseOptions>() with
        {
            Region = "gru",
            MachineId = "gru-1",
            DataDirectory = Path.Combine(Path.GetTempPath(), "terrabase-slot-" + EntityIds.New()),
            NatsUrl = nats.Url,
            NatsToken = nats.Token,
        };
        await using var services = new ServiceCollection().AddSingleton(world.Api.Server.Services.GetRequiredService<ITransactionCrypto>()).AddMetrics().BuildServiceProvider();
        var home = new HomePush(options, new OriginPeer(), new HomeCatalog(), world.Api.Server.Services.GetRequiredService<IDatabaseReplicas>(),
            apply, new FixedClock(), new QuietAck(), new RegionalOwners(options), services, services.GetRequiredService<IMeterFactory>(),
            TimeProvider.System, NullLogger<HomePush>.Instance);
        await home.StartAsync(token);
        try
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(home.Started), TimeSpan.FromSeconds(30));
            var crypto = world.Api.Server.Services.GetRequiredService<ITransactionCrypto>();
            var js = new NatsJSContext(world.Client);
            await world.Client.ConnectAsync();
            for (var i = 0; i < 16; i++)
            {
                var database = new TenantContext(world.Database.OrganizationId, PlanTier.Hobby, world.Database.ProjectId, EntityIds.New().ToString());
                await PublishAsync(js, crypto, database, token);
            }

            await filled.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            await PublishAsync(js, crypto, world.Database, token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
        }
        finally
        {
            await home.StopAsync(CancellationToken.None);
            home.Dispose();
        }
    }

    private static HomeApplyResult Confirmed(RegionTransaction transaction) => new(transaction.Id!, WriteStates.Confirmed, [], []);

    private static async Task PublishAsync(NatsJSContext js, ITransactionCrypto crypto, TenantContext database, CancellationToken token)
    {
        var transaction = new RegionTransaction(Ulid.NewUlid().ToString(), "sjc", new HlcTimestamp(1, 0, "sjc-1"), []);
        var payload = await crypto.SealAsync(database, HomePush.Encode(transaction), token);
        var published = await js.PublishAsync(PushNames.Subject("sjc", "gru", database), payload, cancellationToken: token);
        published.EnsureSuccess();
    }

    private sealed class CountingApply(Func<TenantContext, RegionTransaction, HomeApplyResult?> decide) : IHomeApply
    {
        public ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken) =>
            ValueTask.FromResult(decide(database, transaction));

        public ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<HomeApplyResult?>(null);

        public ValueTask PrepareAsync(TenantContext database, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StampHomeWritesAsync(IStorageTransaction transaction, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<bool> ClaimAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class HomeCatalog : IReplicaCatalog
    {
        public ValueTask<DatabaseRecordResult> FindAsync(TenantContext database, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DatabaseRecordResult>(new TenantDatabaseRecord("main", new DatabasePlacement("gru", ["sjc"], 1)));

        public ValueTask<ReadDatabaseRecordsResult> ListHomedAsync(string homeRegion, TenantContext? after, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> RecordAsync(ReplicaCatalogEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdatePlacementAsync(TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ForgetDatabaseResult> ForgetAsync(TenantContext database, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class OriginPeer : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([new RegionPeer("sjc", new Uri("https://sjc.peer.test"))]);
    }

    private sealed class FixedClock : IHlcClock
    {
        public ValueTask<HlcTimestamp> NextAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new HlcTimestamp(1, 0, "gru-1"));

        public ValueTask<HlcTimestamp> ObserveAsync(HlcTimestamp remote, CancellationToken cancellationToken) => NextAsync(cancellationToken);
    }

    private sealed class QuietAck : IInRegionAck
    {
        public bool PartnerHealthy => true;

        public bool QuorumHealthy => true;

        public ValueTask<bool> HoldAsync(TenantContext database, string home, ulong sequence, byte[] payload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public void Release(TenantContext database, ulong through)
        {
        }
    }
}
