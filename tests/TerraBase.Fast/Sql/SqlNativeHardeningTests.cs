using System.Diagnostics;
using System.Net;
using Bogus;
using TerraBase.Testing;
using static TerraBase.Fast.Sql.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlNativeHardeningTests
{
    private const string Endless = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n) SELECT sum(x) FROM n";

    private static readonly Faker Faker = new();

    [Test]
    public async Task Engine_tables_are_refused_wherever_their_names_appear()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE audit (id INTEGER PRIMARY KEY, note TEXT)");

        var offset = await db.RefusedAsync("UPDATE OR IGNORE commit_offset SET value = 0");
        var quotedStats = await db.RefusedAsync("UPDATE OR IGNORE '_tb_collection_stats' SET bytes = 0");
        var commaJoin = await db.RefusedAsync("SELECT count(*) FROM sqlite_schema, 'turso_cdc'");
        var trigger = await db.RefusedAsync("CREATE TRIGGER wipe AFTER INSERT ON audit BEGIN DELETE FROM _tb_state; END");

        foreach (var refused in new[] { offset, quotedStats, commaJoin, trigger })
        {
            await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.BadRequest, "SQLITE_ERROR"));
        }

        await Assert.That((await db.OkAsync("SELECT count(*) FROM sqlite_schema WHERE name = 'wipe'"))[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(0);
    }

    [Test]
    public async Task An_endless_autocommit_statement_is_interrupted_within_the_bound()
    {
        await using var db = await StartAsync();
        await using var connection = await db.Postgres.OpenConnectionAsync();
        var clock = Stopwatch.StartNew();

        var state = await StateAsync(connection, Endless);

        await Assert.That(state).IsEqualTo("57014");
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));
        await Assert.That(await ScalarAsync(connection, "SELECT 40 + 2")).IsEqualTo(42);
    }

    [Test]
    public async Task An_endless_statement_of_an_idle_transaction_is_interrupted_at_expiry_and_the_writer_is_released()
    {
        await using var db = await StartAsync();
        await using var idle = await db.Postgres.OpenConnectionAsync();
        await using var other = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(idle, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");
        var clock = Stopwatch.StartNew();

        await PostgresSql.ExecuteAsync(idle, "BEGIN");
        await PostgresSql.ExecuteAsync(idle, "INSERT INTO notes (id, body) VALUES (1, $1)", Faker.Lorem.Sentence());
        var endless = StateAsync(idle, Endless);
        var written = await StateAsync(other, "INSERT INTO notes (id, body) VALUES (2, $1)", Faker.Lorem.Sentence());
        var interrupted = await endless;

        await Assert.That(written).IsNull();
        await Assert.That(interrupted).IsEqualTo("57014");
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));
        await Assert.That(await ScalarAsync(other, "SELECT count(*) FROM notes WHERE id = 1")).IsEqualTo(0);
    }

    [Test]
    public async Task An_insert_or_rollback_duplicate_integer_key_answers_the_primary_key_code()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE keys (id INTEGER PRIMARY KEY)", "INSERT INTO keys VALUES (1)");

        var duplicate = await db.RefusedAsync("INSERT OR ROLLBACK INTO keys VALUES (1)");

        await Assert.That(duplicate.Code).IsEqualTo("SQLITE_CONSTRAINT_PRIMARYKEY");
        await Assert.That(await db.CountAsync("keys")).IsEqualTo(1);
    }
}
