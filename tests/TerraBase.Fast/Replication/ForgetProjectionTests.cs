using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Primitives;

namespace TerraBase.Fast.Replication;

public sealed class ForgetProjectionTests
{
    [Test]
    public async Task Forgetting_an_index_definition_removes_its_physical_index_across_restart()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        await RepairAsync(machine, new RepairState(DocumentDelta.Indexes, "by_value", Put(1000, """{"collection":"items","fields":["value"]}""")));
        await using (var before = await (await machine.StoreAsync()).BeginTransactionAsync(write: false, token))
        {
            await Assert.That((await before.ListIndexesAsync(token)).Single().Name).IsEqualTo("by_value");
        }

        await ForgetAsync(machine, (DocumentDelta.Indexes, "by_value"));
        await Assert.That(await machine.HasStateAsync(DocumentDelta.Indexes, "by_value")).IsFalse();
        foreach (var restart in new[] { false, true })
        {
            if (restart)
            {
                await machine.StopAsync();
                await machine.StartAsync();
            }

            await using var read = await (await machine.StoreAsync()).BeginTransactionAsync(write: false, token);
            await Assert.That(await read.ListIndexesAsync(token)).IsEmpty();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Forgetting_a_unique_definition_or_its_winner_restores_the_retained_loser(bool forgetDefinition)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var ids = new[] { EntityIds.New().ToString(), EntityIds.New().ToString() }.Order(StringComparer.Ordinal).ToArray();
        await RepairAsync(machine,
            new(DocumentDelta.Unique, "items", Put(1000, """{"constraints":[{"name":"key","fields":["value"],"normalization":"none"}]}""")),
            new("items", ids[0], Put(1000, """{"value":1}""")),
            new("items", ids[1], Put(2000, """{"value":1}""")));
        await using (var before = await (await machine.StoreAsync()).BeginTransactionAsync(write: false, token))
        {
            await Assert.That((await before.GetAsync("items", ids[0], token))!.UniqueConflict).IsFalse();
            await Assert.That((await before.GetAsync("items", ids[1], token))!.UniqueConflict).IsTrue();
        }

        var (collection, id) = forgetDefinition ? (DocumentDelta.Unique, "items") : ("items", ids[0]);
        await ForgetAsync(machine, (collection, id));
        await Assert.That(await machine.HasStateAsync(collection, id)).IsFalse();
        foreach (var restart in new[] { false, true })
        {
            if (restart)
            {
                await machine.StopAsync();
                await machine.StartAsync();
            }

            await using var read = await (await machine.StoreAsync()).BeginTransactionAsync(write: false, token);
            var loser = await read.GetAsync("items", ids[1], token);
            await Assert.That(loser).IsNotNull();
            await Assert.That(loser!.UniqueConflict).IsFalse();
            await Assert.That(await machine.ReadAsync(ids[1])).IsEqualTo("""{"value":1}""");
            if (forgetDefinition)
            {
                await Assert.That((await read.GetAsync("items", ids[0], token))!.UniqueConflict).IsFalse();
            }
            else
            {
                await Assert.That(await read.GetAsync("items", ids[0], token)).IsNull();
            }
        }
    }

    [Test]
    [Arguments(0UL)]
    [Arguments(2500UL)]
    [Arguments(4000UL)]
    public async Task Forgetting_missing_collection_keys_removes_old_projections_and_preserves_source_present_documents(ulong survivorStamp)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var old = EntityIds.New().ToString();
        var newer = EntityIds.New().ToString();
        await RepairAsync(machine,
            new(DocumentDelta.Collections, "items", Put(1000, $$"""{"created_at":1,"entity_id":"{{EntityIds.New()}}"}""")),
            new("items", old, Put(2000, """{"old":true}""")));
        if (survivorStamp > 0)
        {
            await RepairAsync(machine, new RepairState("items", newer, Put(survivorStamp, """{"new":true}""")));
        }

        await ForgetAsync(machine, (DocumentDelta.Collections, "items"), ("items", old));
        await Assert.That(await machine.HasStateAsync(DocumentDelta.Collections, "items")).IsFalse();
        await Assert.That(await machine.HasStateAsync("items", old)).IsFalse();
        await Assert.That(await machine.ReadAsync(old)).IsNull();
        await machine.StopAsync();
        await machine.StartAsync();
        await Assert.That(await machine.ReadAsync(old)).IsNull();
        if (survivorStamp > 0)
        {
            await Assert.That(await machine.ReadAsync(newer)).IsEqualTo("""{"new":true}""");
            await Assert.That(await machine.HasStateAsync("items", newer)).IsTrue();
        }
        else
        {
            await using var read = await (await machine.StoreAsync()).BeginTransactionAsync(write: false, token);
            await Assert.That((await read.ListCollectionsAsync(null, 100, token)).Collections.Any(collection => collection.Name == "items")).IsFalse();
        }
    }

    [Test]
    public async Task Forgetting_only_collection_metadata_preserves_its_retained_member_across_restart()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await PeerStreamTests.Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var id = EntityIds.New().ToString();
        await RepairAsync(machine,
            new(DocumentDelta.Collections, "items", Put(1000, $$"""{"created_at":1,"entity_id":"{{EntityIds.New()}}"}""")),
            new("items", id, Put(2500, """{"retained":true}""")));

        await ForgetAsync(machine, (DocumentDelta.Collections, "items"));
        foreach (var restart in new[] { false, true })
        {
            if (restart)
            {
                await machine.StopAsync();
                await machine.StartAsync();
            }

            await Assert.That(await machine.HasStateAsync(DocumentDelta.Collections, "items")).IsFalse();
            await Assert.That(await machine.HasStateAsync("items", id)).IsTrue();
            await Assert.That(await machine.ReadAsync(id)).IsEqualTo("""{"retained":true}""");
            await using var read = await (await machine.StoreAsync()).BeginTransactionAsync(write: false, token);
            await Assert.That((await read.ListCollectionsAsync(null, 100, token)).Collections.Any(collection => collection.Name == "items")).IsTrue();
        }
    }

    private static async Task RepairAsync(PeerStreamTests.Machine machine, params RepairState[] states) =>
        _ = await machine.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(
            machine.Cluster.Database, (await machine.StoreAsync()).Incarnation, states, TestContext.Current!.Execution.CancellationToken);

    private static async Task ForgetAsync(PeerStreamTests.Machine machine, params (string Collection, string Id)[] keys)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var engine = machine.Api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(machine.Cluster.Database, token))!;
        await using var forget = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, token);
        await Assert.That(await forget.ForgetAsync(keys, new Hlc(3000), token)).IsEqualTo(keys.Length);
        _ = await forget.CommitAsync(token);
    }

    private static DocumentState Put(ulong stamp, string json)
    {
        using var body = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(new(new Hlc(stamp), ReplicaId.Of(99, 1, 0)), body.RootElement);
    }
}
