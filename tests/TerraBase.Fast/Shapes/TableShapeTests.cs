using System.Globalization;
using System.Net;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Shapes;

public sealed class TableShapeTests
{
    private const string Items = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score REAL, data BLOB, note TEXT)";

    [Test]
    public async Task A_client_syncs_snapshot_then_chunks_then_parks_live_and_sees_inserts_updates_and_deletes()
    {
        await using var db = await StartAsync();
        await db.OkAsync(Items);
        await db.OkAsync(Stmt("INSERT INTO items (id, name) VALUES (1, ?), (2, ?), (3, ?)", "a", "b", "c"));
        var client = Client(db);
        var synced = await client.SyncAsync();
        await Assert.That(client.Equals(await TableShapeClient.TruthAsync(db, "items"))).IsTrue();
        await Assert.That(synced.UpToDate).IsEqualTo("true");

        await db.OkAsync(Stmt("UPDATE items SET name = ? WHERE id = 2", "bee"), Stmt("DELETE FROM items WHERE id = 3"));
        var chunk = await client.ReadAsync(client.NextOffset);
        await Assert.That(chunk.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(chunk.Ops.Select(op => (op.GetProperty("op").GetString()!, op.GetProperty("key").GetString()!)).ToList())
            .IsEquivalentTo([("update", "2"), ("delete", "3")]);
        await Assert.That(chunk.Json.GetProperty("changes").GetArrayLength()).IsEqualTo(1).Because("one commit is one change");

        var parked = client.LiveAsync((await client.CatchUpAsync()).NextOffset!);
        await ParkedAsync(db, readers: 1);
        await db.OkAsync(Stmt("INSERT INTO items (id, name, score) VALUES (9, ?, 1.5)", "live"));
        var answer = await parked;
        await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(answer.UpToDate).IsEqualTo("true");
        await Assert.That(answer.Cursor).IsNotNull();
        await Assert.That(answer.Ops.Single().GetProperty("row").GetProperty("score").GetDouble()).IsEqualTo(1.5);
        await Assert.That(client.Equals(await TableShapeClient.TruthAsync(db, "items"))).IsTrue();
        await CacheControlAssert.EqualAsync(answer.CacheControl);
    }

    [Test]
    public async Task Upserts_under_full_change_capture_commit_and_reach_the_shape_as_insert_then_update()
    {
        await using var db = await StartAsync();
        await db.OkAsync(Items);
        var client = Client(db);
        await client.SyncAsync();

        const string Upsert = "INSERT INTO items (id, name, note) VALUES (?, ?, ?) ON CONFLICT (id) DO UPDATE SET name = excluded.name, note = excluded.note";
        await db.OkAsync(Stmt(Upsert, 4, "first", "inserted"));
        await db.OkAsync(Stmt(Upsert, 4, "second", "updated"), Stmt(Upsert, 5, "other", null));
        List<(string, string)> ops = [];
        ShapeReply chunk;
        do
        {
            chunk = await client.ReadAsync(client.NextOffset);
            await Assert.That(chunk.Status).IsEqualTo(HttpStatusCode.OK);
            ops.AddRange(chunk.Ops.Select(op => (op.GetProperty("op").GetString()!, op.GetProperty("key").GetString()!)));
        }
        while (chunk.UpToDate != "true");

        await Assert.That(ops).IsEquivalentTo([("insert", "4"), ("update", "4"), ("insert", "5")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(client.State["4"]["name"]!.GetValue<string>()).IsEqualTo("second");
        await Assert.That(client.Equals(await TableShapeClient.TruthAsync(db, "items"))).IsTrue();
    }

    [Test]
    public async Task Schema_changes_answer_must_refetch_with_a_new_handle()
    {
        await using var db = await StartAsync();
        await db.OkAsync(Items);
        await db.OkAsync(Stmt("INSERT INTO items (id, name) VALUES (1, ?)", "a"));
        var client = Client(db);
        await client.SyncAsync();
        var first = client.Handle;

        var at = client.NextOffset;
        await db.OkAsync("DROP TABLE items", Items);
        await db.OkAsync(Stmt("INSERT INTO items (id, name) VALUES (5, ?)", "new"));
        var recreated = await client.ReadAsync(at);
        await Assert.That((recreated.Status, recreated.Code)).IsEqualTo((HttpStatusCode.Conflict, "must_refetch"))
            .Because("a table dropped and created again with the same definition is a new lineage");
        await Assert.That(recreated.Handle).IsNotEqualTo(first);
        await client.SyncAsync();
        await Assert.That(client.Handle).IsEqualTo(recreated.Handle);
        await Assert.That(client.State.Keys.ToList()).IsEquivalentTo(["5"]);

        var second = client.Handle;
        await db.OkAsync("ALTER TABLE items ADD COLUMN extra INTEGER DEFAULT 7");
        var altered = await client.ReadAsync(client.NextOffset);
        await Assert.That((altered.Status, altered.Code)).IsEqualTo((HttpStatusCode.Conflict, "must_refetch"));
        await Assert.That(altered.Handle).IsNotEqualTo(second);
        await client.SyncAsync();
        await Assert.That(client.State["5"]["extra"]!.GetValue<long>()).IsEqualTo(7);
        await Assert.That(client.Equals(await TableShapeClient.TruthAsync(db, "items"))).IsTrue();

        var stale = await client.GetAsync(client.Uri(client.NextOffset, live: false, handle: first));
        await Assert.That(stale.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(stale.Handle).IsEqualTo(client.Handle);
    }

    private static TableShapeClient Client(SqlNativeDatabase db) => new(db.Reader, db.Database.Data, "items");

    private static Task ParkedAsync(SqlNativeDatabase db, int readers) =>
        TerraBaseTestServer.EventuallyAsync(async token =>
            (await db.Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token)).Split('\n')
                .Where(line => line.StartsWith("terrabase_shape_requests_waiting", StringComparison.Ordinal))
                .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture)) >= readers);
}
