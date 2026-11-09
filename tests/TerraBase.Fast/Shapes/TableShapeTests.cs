using System.Globalization;
using System.Net;
using Bogus;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Shapes;

public sealed class TableShapeTests
{
    private const string Items = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score REAL, data BLOB, note TEXT)";

    [Test]
    public async Task A_client_syncs_snapshot_then_chunks_then_parks_live_and_sees_inserts_updates_and_deletes()
    {
        // This journey observes the host-wide parked-reader gauge.
        await using var db = await StartIsolatedAsync();
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

    private static TableShapeClient Client(SqlNativeDatabase db, string filter) => new(db.Reader, db.Database.Data, "items", filter);

    private static TableShapeClient Client(SqlNativeDatabase db) => new(db.Reader, db.Database.Data, "items");

    private static Task ParkedAsync(SqlNativeDatabase db, int readers) =>
        TerraBaseTestServer.EventuallyAsync(async token =>
            (await db.Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token)).Split('\n')
                .Where(line => line.StartsWith("terrabase_shape_requests_waiting", StringComparison.Ordinal))
                .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture)) >= readers);
}
