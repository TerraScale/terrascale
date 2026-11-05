using System.Globalization;
using System.Net;
using Bogus;
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
        await CacheControlAssert.EqualAsync(answer.CacheControl, "private, max-age=1, stale-while-revalidate=1");
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

    [Test]
    public async Task A_filtered_shape_carries_its_columns_and_moves_rows_in_and_out_as_they_start_and_stop_matching()
    {
        await using var db = await StartAsync();
        await db.OkAsync(Items);
        await db.OkAsync(Stmt("INSERT INTO items (id, name, score) VALUES (1, ?, 5), (2, ?, 50), (3, ?, 70)", "low", "high", "top"));
        var client = Client(db, "&where=" + Uri.EscapeDataString("score >= 10 AND name != 'hidden'") + "&columns=name,score");
        await client.SyncAsync();
        await Assert.That(client.Equals(await TableShapeClient.TruthAsync(db, "items", "name, score", "score >= 10 AND name != 'hidden'"))).IsTrue();
        await Assert.That(client.State.Keys.ToList()).IsEquivalentTo(["2", "3"]);

        await db.OkAsync(Stmt("UPDATE items SET score = 20 WHERE id = 1"), Stmt("UPDATE items SET score = 1 WHERE id = 2"),
            Stmt("UPDATE items SET note = ? WHERE id = 3", "ignored"), Stmt("INSERT INTO items (id, name, score) VALUES (4, ?, 90), (5, ?, 0)", "new", "out"),
            Stmt("UPDATE items SET name = ? WHERE id = 3", "hidden"));
        List<(string, string, string?)> ops = [];
        ShapeReply chunk;
        do
        {
            chunk = await client.ReadAsync(client.NextOffset);
            await Assert.That(chunk.Status).IsEqualTo(HttpStatusCode.OK);
            ops.AddRange(chunk.Ops.Select(op => (op.GetProperty("op").GetString()!, op.GetProperty("key").GetString()!,
                op.TryGetProperty("move", out var move) ? move.GetString() : null)));
        }
        while (chunk.UpToDate != "true");

        await Assert.That(ops).IsEquivalentTo([("insert", "1", "in"), ("delete", "2", "out"), ("insert", "4", null), ("delete", "3", "out")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("the note-only update changes no shape column and row 5 never matches");
        await Assert.That(client.State["1"].Select(pair => pair.Key).ToList()).IsEquivalentTo(["name", "score"]);
        await Assert.That(client.Equals(await TableShapeClient.TruthAsync(db, "items", "name, score", "score >= 10 AND name != 'hidden'"))).IsTrue();
    }

    [Test]
    public async Task A_schema_change_gives_a_filtered_shape_a_new_handle_and_409_for_the_old_one()
    {
        await using var db = await StartAsync();
        await db.OkAsync(Items);
        await db.OkAsync(Stmt("INSERT INTO items (id, name, score) VALUES (1, ?, 50)", "a"));
        var client = Client(db, "&where=" + Uri.EscapeDataString("score > 10") + "&columns=name");
        await client.SyncAsync();
        var first = client.Handle;
        var table = Client(db);
        await table.SyncAsync();
        await Assert.That(table.Handle).IsNotEqualTo(first).Because("each where and columns pair has its own handle");

        await db.OkAsync("ALTER TABLE items ADD COLUMN extra INTEGER DEFAULT 7");
        var stale = await client.ReadAsync(client.NextOffset);
        await Assert.That((stale.Status, stale.Code)).IsEqualTo((HttpStatusCode.Conflict, "must_refetch"));
        await Assert.That(stale.Handle).IsNotEqualTo(first);
        await client.SyncAsync();
        await Assert.That(client.Handle).IsEqualTo(stale.Handle);
        await Assert.That(client.Equals(await TableShapeClient.TruthAsync(db, "items", "name", "score > 10"))).IsTrue();
    }

    [Test]
    public async Task Unsafe_or_unknown_where_and_columns_are_refused()
    {
        await using var db = await StartAsync();
        await db.OkAsync(Items);
        foreach (var (query, code) in ((string, string)[])[
            ("where=" + Uri.EscapeDataString("id IN (SELECT id FROM items)"), "unsafe_where"),
            ("where=" + Uri.EscapeDataString("random() > 0"), "unsafe_where"),
            ("where=" + Uri.EscapeDataString("id = ?"), "unsafe_where"),
            ("where=" + Uri.EscapeDataString("id = 1; DROP TABLE items"), "unsafe_where"),
            ("where=" + Uri.EscapeDataString("id = (1"), "unsafe_where"),
            ("where=" + Uri.EscapeDataString("missing = 1"), "unsafe_where"),
            ("columns=name,missing", "unknown_column")])
        {
            var reply = await Client(db).GetAsync(new Uri($"{Client(db).Path}?offset=-1&{query}", UriKind.Relative));
            await Assert.That((reply.Status, reply.Code)).IsEqualTo((HttpStatusCode.BadRequest, code)).Because(query);
        }

        await Assert.That(await db.CountAsync("items")).IsEqualTo(0);
    }

    private static TableShapeClient Client(SqlNativeDatabase db, string filter) => new(db.Reader, db.Database.Data, "items", filter);

    private static TableShapeClient Client(SqlNativeDatabase db) => new(db.Reader, db.Database.Data, "items");

    private static Task ParkedAsync(SqlNativeDatabase db, int readers) =>
        TerraBaseTestServer.EventuallyAsync(async token =>
            (await db.Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token)).Split('\n')
                .Where(line => line.StartsWith("terrabase_shape_requests_waiting", StringComparison.Ordinal))
                .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture)) >= readers);
}
