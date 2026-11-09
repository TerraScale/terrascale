using Bogus;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Slow.Shapes;

public sealed class TableShapeWorkloadTests
{
    private const string Items = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score REAL, data BLOB, note TEXT)";

    [Test]
    public async Task A_shape_equals_its_table_after_20k_operations()
    {
        await using var db = await StartAsync();
        await db.Api.SetPlanAsync(db.Api.User, db.Database.Organization, "pro");
        await db.OkAsync(Items);
        var faker = new Faker { Random = new Randomizer(8_675_309) };
        var early = Client(db);
        await early.SyncAsync();
        TableShapeClient? late = null;
        const int Keys = 2_000;
        const int Operations = 20_000;
        var done = 0;
        var requests = 0;
        while (done < Operations)
        {
            var batch = Math.Min(Operations - done, faker.Random.Int(1, 20));
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

    private static TableShapeClient Client(SqlNativeDatabase db) => new(db.Reader, db.Database.Data, "items");
}
