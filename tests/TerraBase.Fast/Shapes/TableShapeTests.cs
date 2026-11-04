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
    public async Task A_shape_equals_its_table_after_100k_operations()
    {
        await using var db = await StartAsync();
        await db.OkAsync(Items);
        var faker = new Faker { Random = new Randomizer(8_675_309) };
        var early = Client(db);
        await early.SyncAsync();
        TableShapeClient? late = null;
        const int Keys = 2_000;
        const int Operations = 100_000;
        var done = 0;
        var requests = 0;
        while (done < Operations)
        {
            var batch = Math.Min(Operations - done, faker.Random.Int(1, 250));
            var statements = new object[batch];
            for (var index = 0; index < batch; index++)
            {
                var id = faker.Random.Int(1, Keys);
                statements[index] = faker.Random.Int(0, 99) switch
                {
                    < 45 => Stmt("INSERT INTO items (id, name, score, data, note) VALUES (?, ?, ?, ?, ?) ON CONFLICT (id) DO UPDATE SET name = excluded.name, score = excluded.score, data = excluded.data, note = excluded.note",
                        id, faker.Lorem.Word(), faker.Random.Bool() ? faker.Random.Double(-1e6, 1e6) : null, faker.Random.Bool() ? Blob(faker.Random.Bytes(faker.Random.Int(0, 24))) : null,
                        faker.Random.Bool(0.2f) ? null : faker.Lorem.Sentence()),
                    < 70 => Stmt("UPDATE items SET score = coalesce(score, 0) + ?, note = ? WHERE id = ?", faker.Random.Int(-50, 50), faker.Lorem.Word(), id),
                    < 80 => Stmt("UPDATE items SET id = ? WHERE id = ? AND NOT EXISTS (SELECT 1 FROM items WHERE id = ?)", id + Keys, id, id + Keys),
                    < 85 => Stmt("INSERT OR REPLACE INTO items (id, name) VALUES (?, ?)", id, faker.Lorem.Word()),
                    _ => Stmt("DELETE FROM items WHERE id = ? OR id = ?", id, id + Keys),
                };
            }

            await db.OkAsync(statements);
            done += batch;
            requests++;
            if (requests % 25 == 0)
            {
                await early.CatchUpAsync();
            }

            if (requests == 300)
            {
                late = Client(db);
                await late.SyncAsync();
            }

            if (late is not null && requests % 40 == 0)
            {
                await late.CatchUpAsync();
            }
        }

        await early.CatchUpAsync();
        await late!.CatchUpAsync();
        var fresh = Client(db);
        await fresh.SyncAsync();
        var truth = await TableShapeClient.TruthAsync(db, "items");

        await Assert.That(truth.Count).IsGreaterThan(100);
        await Assert.That(early.Equals(truth)).IsTrue().Because("a client that synced before the workload converges through chunks alone");
        await Assert.That(late.Equals(truth)).IsTrue().Because("a client that bootstrapped mid-workload converges through snapshot and chunks");
        await Assert.That(fresh.Equals(truth)).IsTrue().Because("a fresh snapshot equals the table");
        await Assert.That(early.Handle).IsEqualTo(fresh.Handle);
    }

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
