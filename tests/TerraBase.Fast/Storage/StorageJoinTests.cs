using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

/// <summary>
/// The receive path of replication (docs/terrabase/multi-region-replication.md section 9): replicated deltas join into a
/// client database through <see cref="IStorageTransaction.JoinAsync"/>, in any order and any number of times.
/// </summary>
public sealed class StorageJoinTests
{
    private static readonly ReplicaId Gru = ReplicaId.Of(1, 1, 0);
    private static readonly ReplicaId Sjc = ReplicaId.Of(2, 2, 0);
    private static readonly ulong Now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;

    [Test]
    public async Task Deltas_joined_in_any_order_and_repeated_end_in_the_same_document_and_collection()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var id = EntityIds.New().ToString();
        (string Collection, string Id, ulong Stamp, ReplicaId Node, DocumentState State)[] deltas =
        [
            Metadata(Gru, Now, $$"""{"created_at":1,"entity_id":"{{EntityIds.New()}}"}"""),
            ("items", id, Now, Gru, Put(Gru, Now, """{"a":"gru","b":"gru"}""")),
            ("items", id, Now + 1, Sjc, Set(Sjc, Now + 1, "a", "\"sjc\"")),
            ("items", id, Now + 2, Gru, Set(Gru, Now + 2, "b", "\"gru-2\"")),
        ];

        var inOrder = await DatabaseAsync(server);
        foreach (var delta in deltas)
        {
            await Assert.That(await JoinAsync(server, inOrder, delta)).IsTrue();
        }

        // Reversed and repeated: the document's own deltas arrive before its collection's metadata document.
        var reversed = await DatabaseAsync(server);
        foreach (var delta in deltas.Reverse().Concat(deltas))
        {
            _ = await JoinAsync(server, reversed, delta);
        }

        await Assert.That(await JoinAsync(server, reversed, deltas[1])).IsFalse();
        foreach (var database in (TenantContext[])[inOrder, reversed])
        {
            await Assert.That(await ReadAsync(server, database, id)).IsEqualTo("""{"a":"sjc","b":"gru-2"}""");
        }

        await Assert.That(await CollectionIdAsync(server, reversed)).IsEqualTo(await CollectionIdAsync(server, inOrder));
    }

    [Test]
    public async Task A_dropped_collection_ignores_older_deltas_and_a_newer_write_brings_it_back()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var database = await DatabaseAsync(server);
        var id = EntityIds.New().ToString();
        _ = await JoinAsync(server, database, Metadata(Gru, Now, $$"""{"created_at":1,"entity_id":"{{EntityIds.New()}}"}"""));
        _ = await JoinAsync(server, database, ("items", id, Now, Gru, Put(Gru, Now, """{"a":1}""")));
        _ = await JoinAsync(server, database, (DocumentDelta.Collections, "items", Now + 5, Sjc, DocumentState.Bottom.Delete(new(new Hlc(Now + 5), Sjc))));
        await Assert.That(await CollectionIdAsync(server, database)).IsNull();

        // Written before the drop on a machine that had not seen it yet: dominated.
        await Assert.That(await JoinAsync(server, database, ("items", id, Now + 3, Gru, Set(Gru, Now + 3, "b", "2")))).IsFalse();
        await Assert.That(await CollectionIdAsync(server, database)).IsNull();

        // Written after the drop: the collection comes back with only that write.
        await Assert.That(await JoinAsync(server, database, ("items", id, Now + 7, Gru, Set(Gru, Now + 7, "c", "3")))).IsTrue();
        await Assert.That(await ReadAsync(server, database, id)).IsEqualTo("""{"c":3}""");
    }

    [Test]
    public async Task A_unique_collision_from_a_peer_hides_the_loser_instead_of_failing()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var database = await DatabaseAsync(server);
        var store = await OpenAsync(server, database);
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            await transaction.SetUniqueConstraintsAsync("items", [new UniqueConstraint("email", ["email"])], CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        var (first, second) = ("01AAAAAAAAAAAAAAAAAAAAAAAA", "01BBBBBBBBBBBBBBBBBBBBBBBB");
        _ = await JoinAsync(server, database, ("items", second, Now, Sjc, Put(Sjc, Now, """{"email":"a@b.c"}""")));
        _ = await JoinAsync(server, database, ("items", first, Now + 1, Gru, Put(Gru, Now + 1, """{"email":"a@b.c"}""")));

        // The smallest id wins; the other is kept aside as a conflict, out of the collection's table.
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await read.GetAsync("items", first, CancellationToken.None))!.UniqueConflict).IsFalse();
        await Assert.That((await read.GetAsync("items", second, CancellationToken.None))!.UniqueConflict).IsTrue();
    }

    [Test]
    public async Task A_refill_forgetting_a_unique_winner_shows_the_hidden_loser()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var database = await DatabaseAsync(server);
        var store = await OpenAsync(server, database);
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            await transaction.SetUniqueConstraintsAsync("items", [new UniqueConstraint("email", ["email"])], CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        var (first, second) = ("01AAAAAAAAAAAAAAAAAAAAAAAA", "01BBBBBBBBBBBBBBBBBBBBBBBB");
        _ = await JoinAsync(server, database, ("items", second, Now, Sjc, Put(Sjc, Now, """{"email":"a@b.c"}""")));
        _ = await JoinAsync(server, database, ("items", first, Now + 1, Gru, Put(Gru, Now + 1, """{"email":"a@b.c"}""")));

        // The peer a refill copies from collected the winner; forgetting it here leaves the loser the only holder of the key.
        var held = (TursoDatabaseStore)(await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
        await using (var transaction = (TursoStorageTransaction)await held.BeginDurableTransactionAsync(CancellationToken.None))
        {
            await Assert.That(await transaction.ForgetAsync([("items", first)], new Hlc(Now + 2), CancellationToken.None)).IsEqualTo(1);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.GetAsync("items", first, CancellationToken.None)).IsNull();
        await Assert.That((await read.GetAsync("items", second, CancellationToken.None))!.UniqueConflict).IsFalse();
        await Assert.That(await ReadAsync(server, database, second)).IsEqualTo("""{"email":"a@b.c"}""");
    }

    private static DocumentState Put(ReplicaId node, ulong stamp, string json)
    {
        using var body = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(new(new Hlc(stamp), node), body.RootElement);
    }

    private static DocumentState Set(ReplicaId node, ulong stamp, string field, string json)
    {
        using var value = JsonDocument.Parse(json);
        return DocumentState.Bottom.Set(new(new Hlc(stamp), node), field, value.RootElement);
    }

    private static (string, string, ulong, ReplicaId, DocumentState) Metadata(ReplicaId node, ulong stamp, string json) =>
        (DocumentDelta.Collections, "items", stamp, node, Put(node, stamp, json));

    private static async Task<TenantContext> DatabaseAsync(TerraBaseTestServer server)
    {
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await Assert.That(await server.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        return database;
    }

    private static async Task<IDatabaseStore> OpenAsync(TerraBaseTestServer server, TenantContext database) =>
        (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;

    private static async Task<bool> JoinAsync(TerraBaseTestServer server, TenantContext database, (string Collection, string Id, ulong Stamp, ReplicaId Node, DocumentState State) delta)
    {
        await using var transaction = await (await OpenAsync(server, database)).BeginDurableTransactionAsync(CancellationToken.None);
        var stamp = new Hlc(delta.Stamp);
        var joined = await transaction.JoinAsync(delta.Collection, delta.Id, delta.State.DeltaAt(stamp), new EntryVersion(stamp, delta.Node), CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
        return joined;
    }

    private static async Task<string?> ReadAsync(TerraBaseTestServer server, TenantContext database, string id)
    {
        await using var transaction = await (await OpenAsync(server, database)).BeginTransactionAsync(write: false, CancellationToken.None);
        return await transaction.GetAsync("items", id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
    }

    private static async Task<string?> CollectionIdAsync(TerraBaseTestServer server, TenantContext database)
    {
        await using var transaction = await (await OpenAsync(server, database)).BeginTransactionAsync(write: false, CancellationToken.None);
        return (await transaction.GetCollectionAsync("items", CancellationToken.None))?.Id;
    }
}
