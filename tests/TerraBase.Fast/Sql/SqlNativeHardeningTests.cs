using System.Diagnostics;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
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

    [Test]
    public async Task Expiry_that_fires_during_commit_waits_for_it_and_the_database_stays_writable()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var armed = 0;
        string? target = null;
        await using var db = await StartAsync(services =>
        {
            services.AddKeyedSingleton<TimeProvider>(SqlLimits.TransactionClock, clock);
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step == "writer.commit" && database.DatabaseId == Volatile.Read(ref target) && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    clock.Advance(SqlLimits.MaxTransactionTime + TimeSpan.FromSeconds(1));
                }
            });
        });
        Volatile.Write(ref target, db.Database.Id);
        await using var connection = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(connection, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await PostgresSql.ExecuteAsync(connection, "INSERT INTO notes (id, body) VALUES (1, $1)", Faker.Lorem.Sentence());
            Volatile.Write(ref armed, 1);
            await transaction.CommitAsync();
        }

        var fired = Volatile.Read(ref armed) == 0;
        await PostgresSql.ExecuteAsync(connection, "INSERT INTO notes (id, body) VALUES (2, $1)", Faker.Lorem.Sentence());
        await db.OkAsync("INSERT INTO notes (id, body) VALUES (3, 'rest')");

        await Assert.That(fired).IsTrue();
        await Assert.That(await ScalarAsync(connection, "SELECT count(*) FROM notes")).IsEqualTo(3);
    }

    [Test]
    public async Task Cancellations_racing_statement_completion_leave_the_pooled_connection_usable()
    {
        const string Short = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < 2000) SELECT sum(x) FROM n";
        await using var db = await StartAsync();
        await using (var racing = await db.Postgres.OpenConnectionAsync())
        {
            for (var i = 0; i < 200; i++)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromTicks(Faker.Random.Long(0, 30_000)));
                await using var command = new NpgsqlCommand(Short, racing);
                try
                {
                    _ = await command.ExecuteScalarAsync(cancel.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (PostgresException error) when (error.SqlState == "57014")
                {
                }
            }
        }

        await using var plain = await db.Postgres.OpenConnectionAsync();
        var answers = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            answers.Add(await ScalarAsync(plain, "SELECT 1"));
        }

        await Assert.That(answers.Count(answer => answer == 1)).IsEqualTo(50);
    }
}
