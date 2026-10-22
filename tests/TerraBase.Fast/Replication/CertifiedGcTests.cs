using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Primitives;

namespace TerraBase.Fast.Replication;

public sealed class CertifiedGcTests
{
    [Test]
    public async Task Certified_refill_imports_old_baselines_then_applies_the_source_frontier_to_salvage_and_later_repairs()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: false, clock: null,
            configure: (_, services) =>
            {
                foreach (var recovery in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(RebuildRecovery)).ToArray())
                {
                    services.Remove(recovery);
                }
            }, "gru", "sjc", "fra");
        var source = cluster["gru"];
        var receiver = cluster["sjc"];
        var stale = cluster["fra"];
        var deleted = EntityIds.New().ToString();
        var baseline = EntityIds.New().ToString();
        var newer = EntityIds.New().ToString();
        var old = Put(1000, """{"old":true}""");
        await RepairAsync(source, new("items", deleted, old.Delete(At(2000))), new("items", baseline, Put(500, """{"baseline":true}""")));
        await RepairAsync(receiver, new("items", deleted, old), new("items", newer, Put(4000, """{"newer":true}""")));
        await RepairAsync(stale, new RepairState("items", deleted, old));
        var engine = source.Api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var sourceStore = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, token))!;
        await using (var collect = (SqliteStorageTransaction)await sourceStore.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await collect.CollectAsync(new Hlc(3000), token)).IsEqualTo(1);
            _ = await collect.CommitAsync(token);
        }
        await Assert.That(await source.HasStateAsync("items", deleted)).IsFalse();
        var peer = (await cluster.Members.ListAsync(token)).Single(member => member.Name == stale.Name);
        cluster.Members.Leave(stale.Name);
        var incarnation = await receiver.IncarnationAsync();
        await receiver.Api.Server.Services.GetRequiredService<IReplicaCopyFaults>()
            .RecordAsync(cluster.Database, incarnation, "a fixture copy requiring certified refill", token);

        var outcome = await receiver.Api.Server.Services.GetRequiredService<IReplicaRebuild>()
            .RebuildAsync(cluster.Database, incarnation, token);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(await receiver.ReadAsync(baseline)).IsEqualTo("""{"baseline":true}""");
        await Assert.That(await receiver.ReadAsync(newer)).IsEqualTo("""{"newer":true}""");
        await Assert.That(await receiver.ReadAsync(deleted)).IsNull();
        cluster.Members.Join(peer);
        await receiver.StopAsync();
        await receiver.StartAsync();
        _ = await receiver.AntiEntropy.PullAsync(cluster.Database, peer, token);
        await Assert.That(await receiver.ReadAsync(deleted)).IsNull();
        await Assert.That(await receiver.HasStateAsync("items", deleted)).IsFalse();
        await Assert.That(await receiver.Api.Server.Services.GetRequiredService<IRebuildRepair>()
            .RefillRebuildAsync(cluster.Database, await receiver.IncarnationAsync(), [], null, [], default, token)).IsNull();
        await Assert.That(await receiver.ReadAsync(baseline)).IsEqualTo("""{"baseline":true}""");
        await Assert.That(await receiver.ReadAsync(newer)).IsEqualTo("""{"newer":true}""");
    }

    private static async Task RepairAsync(PeerStreamTests.Machine machine, params RepairState[] states) =>
        _ = await machine.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(
            machine.Cluster.Database, (await machine.StoreAsync()).Incarnation, states, TestContext.Current!.Execution.CancellationToken);

    private static EntryVersion At(ulong stamp) => new(new Hlc(stamp), ReplicaId.Of(99, 1, 0));

    private static DocumentState Put(ulong stamp, string json)
    {
        using var body = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(At(stamp), body.RootElement);
    }
}
