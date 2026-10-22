using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Primitives;

namespace TerraBase.Fast.Replication;

public sealed class AntiEntropyGcTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Collected_documents_stay_absent_when_two_healthy_replicas_pull_a_stale_peer(bool restart)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru", "sjc", "fra");
        var stale = cluster["sjc"];
        var id = EntityIds.New().ToString();
        var live = Put(1000, """{"value":"deleted"}""");
        var dead = live.Delete(At(2000));
        await Assert.That(await RepairAsync(stale, new("items", id, live))).IsEqualTo(1);
        foreach (var healthy in new[] { cluster["gru"], cluster["fra"] })
        {
            await Assert.That(await RepairAsync(healthy, new("items", id, dead))).IsEqualTo(1);
            await CollectAsync(healthy, 3000);
            await Assert.That(await healthy.HasStateAsync("items", id)).IsFalse();
            if (restart)
            {
                await healthy.StopAsync();
                await healthy.StartAsync();
            }

            var peer = (await cluster.Members.ListAsync(token)).Single(member => member.Name == stale.Name);
            _ = await healthy.AntiEntropy.PullAsync(cluster.Database, peer, token);
            await Assert.That(await healthy.ReadAsync(id)).IsNull();
            await Assert.That(await healthy.HasStateAsync("items", id)).IsFalse();
        }

        await Assert.That(await stale.ReadAsync(id)).IsNotNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stale_repair_waiting_on_a_collecting_writer_cannot_resurrect_the_document(bool collectFirst)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var id = EntityIds.New().ToString();
        var live = Put(1000, """{"value":"deleted"}""");
        _ = await RepairAsync(machine, new("items", id, live.Delete(At(2000))));
        if (!collectFirst)
        {
            await Assert.That(await RepairAsync(machine, new("items", id, live))).IsEqualTo(0);
        }

        var store = await RawStoreAsync(machine);
        Task<int?> repair;
        await using (var writer = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, token))
        {
            if (collectFirst)
            {
                await Assert.That(await writer.CollectAsync(new Hlc(3000), token)).IsEqualTo(1);
            }

            repair = RepairAsync(machine, new("items", id, live));
            await Assert.That(repair.IsCompleted).IsFalse();
            if (!collectFirst)
            {
                await Assert.That(await writer.CollectAsync(new Hlc(3000), token)).IsEqualTo(1);
            }

            _ = await writer.CommitAsync(token);
        }

        await Assert.That(await repair).IsEqualTo(0);
        await Assert.That(await machine.ReadAsync(id)).IsNull();
        await Assert.That(await machine.HasStateAsync("items", id)).IsFalse();
    }

    [Test]
    public async Task A_rolled_back_collection_does_not_reject_an_old_live_baseline_and_a_lower_horizon_cannot_reopen_collected_history()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var id = EntityIds.New().ToString();
        var baseline = EntityIds.New().ToString();
        var live = Put(1000, """{"value":1}""");
        _ = await RepairAsync(machine, new("items", id, live.Delete(At(2000))));
        var store = await RawStoreAsync(machine);
        await using (var rolledBack = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await rolledBack.CollectAsync(new Hlc(3000), token)).IsEqualTo(1);
        }

        await Assert.That(await RepairAsync(machine, new("items", baseline, live))).IsEqualTo(1);
        await Assert.That(await machine.ReadAsync(baseline)).IsEqualTo("""{"value":1}""");
        await CollectAsync(machine, 3000);
        await using (var lower = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await lower.CollectAsync(new Hlc(1500), token)).IsEqualTo(0);
            _ = await lower.CommitAsync(token);
        }

        await Assert.That(await RepairAsync(machine, new("items", id, live))).IsEqualTo(0);
        await Assert.That(await machine.ReadAsync(id)).IsNull();
        await Assert.That(await RepairAsync(machine, new("items", baseline, live))).IsEqualTo(0);
        await Assert.That(await machine.ReadAsync(baseline)).IsEqualTo("""{"value":1}""");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_mixed_state_preserves_new_fields_without_restoring_fields_older_than_collection(int kind)
    {
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var id = EntityIds.New().ToString();
        var old = Put(1000, """{"old":true}""");
        _ = await RepairAsync(machine, new("items", id, old.Delete(At(2000))));
        await CollectAsync(machine, 3000);
        using var value = JsonDocument.Parse(kind == 1 ? "0" : "true");
        var mixed = kind switch
        {
            1 => old.Set(At(4000), "new", value.RootElement).Increment(At(4001), "new", 1),
            2 => old.Add(At(4000), "new", value.RootElement),
            _ => old.Set(At(4000), "new", value.RootElement),
        };
        var expected = kind switch { 1 => """{"new":1}""", 2 => """{"new":[true]}""", _ => """{"new":true}""" };

        await Assert.That(await RepairAsync(machine, new("items", id, mixed))).IsEqualTo(1);
        await Assert.That(await machine.ReadAsync(id)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_local_recreation_mints_above_a_future_collected_floor_after_restart()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var id = EntityIds.New().ToString();
        var floor = ((ulong)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() << 16) + 3;
        var deleted = Put(floor - 3, """{"old":true}""").Delete(At(floor - 2));
        _ = await RepairAsync(machine, new("items", id, deleted));
        await CollectAsync(machine, floor);
        await machine.StopAsync();
        await machine.StartAsync();

        await using var write = await (await machine.StoreAsync()).BeginTransactionAsync(write: true, token);
        var version = await write.PutAsync("items", id, "{\"local\":true}"u8.ToArray(), token);
        await Assert.That((ulong)version > floor).IsTrue();
        _ = await write.CommitAsync(token);
        await Assert.That(await machine.ReadAsync(id)).IsEqualTo("""{"local":true}""");
    }

    private static async Task<int?> RepairAsync(PeerStreamTests.Machine machine, RepairState state) =>
        await machine.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(
            machine.Cluster.Database, (await machine.StoreAsync()).Incarnation, [state], TestContext.Current!.Execution.CancellationToken);

    private static async Task<SqliteDatabaseStore> RawStoreAsync(PeerStreamTests.Machine machine) =>
        (SqliteDatabaseStore)(await machine.Api.Server.Services.GetRequiredService<SqliteStorageEngine>()
            .OpenDatabaseAsync(machine.Cluster.Database, TestContext.Current!.Execution.CancellationToken))!;

    private static async Task CollectAsync(PeerStreamTests.Machine machine, ulong horizon)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var collect = (SqliteStorageTransaction)await (await RawStoreAsync(machine)).BeginTransactionAsync(write: true, token);
        await Assert.That(await collect.CollectAsync(new Hlc(horizon), token)).IsEqualTo(1);
        _ = await collect.CommitAsync(token);
    }

    private static EntryVersion At(ulong stamp) => new(new Hlc(stamp), ReplicaId.Of(99, 1, 0));

    private static DocumentState Put(ulong stamp, string json)
    {
        using var body = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(At(stamp), body.RootElement);
    }
}
