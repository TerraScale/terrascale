using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Primitives;

namespace TerraBase.Fast.Replication;

public sealed class OrdinaryGcTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Ordinary_refill_preserves_source_present_members_below_its_horizon_when_collection_metadata_was_collected(bool survivor)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru", "sjc");
        var source = cluster["gru"];
        var receiver = cluster["sjc"];
        var missing = EntityIds.New().ToString();
        var retained = EntityIds.New().ToString();
        var metadata = Put(1000, $$"""{"created_at":1,"entity_id":"{{EntityIds.New()}}"}""");
        var old = Put(1500, """{"missing":true}""");
        await RepairAsync(source, new(DocumentDelta.Collections, "items", metadata), new("items", missing, old));
        if (survivor)
        {
            await RepairAsync(source, new RepairState("items", retained, Put(2500, """{"survivor":true}""")));
        }

        await RepairAsync(receiver, new(DocumentDelta.Collections, "items", metadata), new("items", missing, old));
        await RepairAsync(source, new RepairState(DocumentDelta.Collections, "items", metadata.Delete(At(2000))));
        var engine = source.Api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, token))!;
        await using (var collect = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await collect.CollectAsync(new Hlc(3000), token)).IsEqualTo(1);
            _ = await collect.CommitAsync(token);
        }

        await Assert.That(await source.HasStateAsync(DocumentDelta.Collections, "items")).IsFalse();
        await Assert.That(await source.HasStateAsync("items", missing)).IsFalse();
        await Assert.That(await receiver.HasStateAsync(DocumentDelta.Collections, "items")).IsTrue();
        await Assert.That(await receiver.ReadAsync(missing)).IsEqualTo("""{"missing":true}""");
        if (survivor)
        {
            await Assert.That(await source.ReadAsync(retained)).IsEqualTo("""{"survivor":true}""");
            await Assert.That(await receiver.ReadAsync(retained)).IsNull();
        }

        var changed = await receiver.AntiEntropy.RefillAsync(cluster.Database, source.Name, new Hlc(3000),
            await source.IncarnationAsync(), token);

        await Assert.That(changed).IsNotNull();
        foreach (var restart in new[] { false, true })
        {
            if (restart)
            {
                await receiver.StopAsync();
                await receiver.StartAsync();
            }

            await Assert.That(await receiver.HasStateAsync(DocumentDelta.Collections, "items")).IsFalse();
            await Assert.That(await receiver.ReadAsync(missing)).IsNull();
            await Assert.That(await receiver.HasStateAsync("items", missing)).IsFalse();
            await Assert.That(await receiver.ReadAsync(retained)).IsEqualTo(survivor ? """{"survivor":true}""" : null);
            await Assert.That(await receiver.HasStateAsync("items", retained)).IsEqualTo(survivor);
            await using var read = await (await receiver.StoreAsync()).BeginTransactionAsync(write: false, token);
            await Assert.That((await read.ListCollectionsAsync(null, 100, token)).Collections.Any(collection => collection.Name == "items"))
                .IsEqualTo(survivor);
        }
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
