using System.Diagnostics;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TerraBase.Engine;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Slow.Sql;

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
    public async Task A_deferred_foreign_key_violation_fails_the_commit_and_the_database_keeps_accepting_writes()
    {
        await using var db = await StartAsync();
        await db.OkAsync(
            "CREATE TABLE teams (id INTEGER PRIMARY KEY)",
            "CREATE TABLE players (id INTEGER PRIMARY KEY, team INTEGER REFERENCES teams (id) DEFERRABLE INITIALLY DEFERRED)");

        var rest = await db.RefusedAsync("INSERT INTO players VALUES (1, 99)");
        await using var connection = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(connection, "BEGIN");
        await PostgresSql.ExecuteAsync(connection, "INSERT INTO players VALUES (2, 98)");
        var postgres = await StateAsync(connection, "COMMIT");
        await db.OkAsync("INSERT INTO teams VALUES (7)", "INSERT INTO players VALUES (3, 7)");
        await PostgresSql.ExecuteAsync(connection, "INSERT INTO teams VALUES (8)");

        await Assert.That(rest.Code).IsEqualTo("SQLITE_CONSTRAINT_FOREIGNKEY");
        await Assert.That(postgres).IsEqualTo("23503");
        await Assert.That(await db.CountAsync("players")).IsEqualTo(1);
        await Assert.That(await db.CountAsync("teams")).IsEqualTo(2);
    }

    [Test]
    public async Task Values_past_the_result_bound_are_refused_before_they_are_built()
    {
        await using var db = await StartAsync();

        var zeroblob = await db.RefusedAsync("SELECT zeroblob(536870912)");
        var randomblob = await db.RefusedAsync("SELECT length(randomblob(536870912))");
        var doubling = await db.RefusedAsync("WITH RECURSIVE s(x, n) AS (SELECT 'x', 0 UNION ALL SELECT x || x, n + 1 FROM s WHERE n < 40) SELECT max(length(x)) FROM s");
        var hex = await db.RefusedAsync("SELECT length(hex(randomblob(5000000)))");
        var fits = await db.OkAsync("SELECT length(zeroblob(1048576))");

        foreach (var refused in new[] { zeroblob, randomblob, doubling, hex })
        {
            await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "SQLITE_TOOBIG"));
        }

        await Assert.That(fits[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1048576);
    }

    [Test]
    public async Task Sql_storage_counts_against_the_quota_and_deletes_free_it()
    {
        await using var db = await StartAsync(services => services.AddSingleton<IStorageQuota>(provider =>
            new StorageQuota(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<ITenantDirectory>(),
                plan => plan == PlanTier.Hobby ? 64 * 1024 : long.MaxValue)));
        await db.OkAsync("CREATE TABLE files (id INTEGER PRIMARY KEY, data BLOB NOT NULL)");

        string? full = null;
        for (var i = 0; i < 64 && full is null; i++)
        {
            var (status, body) = await db.PostAsync(db.Writer, Stmt("INSERT INTO files (data) VALUES (randomblob(3000))"));
            full = status == HttpStatusCode.OK ? null : body.GetProperty("error").GetProperty("sqlite_code").GetString();
        }

        var rows = await db.CountAsync("files");
        await db.OkAsync("DELETE FROM files");
        await db.OkAsync("INSERT INTO files (data) VALUES (randomblob(3000))");

        await Assert.That(full).IsEqualTo("SQLITE_FULL");
        await Assert.That(rows).IsGreaterThan(0).And.IsLessThan(64);
        await Assert.That(await db.CountAsync("files")).IsEqualTo(1);
    }

    [Test]
    public async Task An_explicit_transaction_and_an_autocommit_writer_of_the_same_organization_both_complete()
    {
        await using var db = await StartAsync();
        await using var explicitWriter = await db.Postgres.OpenConnectionAsync();
        await using var autocommit = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(explicitWriter, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");
        var clock = Stopwatch.StartNew();

        await using var transaction = await explicitWriter.BeginTransactionAsync();
        await PostgresSql.ExecuteAsync(explicitWriter, "INSERT INTO notes (id, body) VALUES (1, $1)", Faker.Lorem.Sentence());
        var waiting = StateAsync(autocommit, "INSERT INTO notes (id, body) VALUES (2, $1)", Faker.Lorem.Sentence());
        await PostgresSql.ExecuteAsync(explicitWriter, "INSERT INTO notes (id, body) VALUES (3, $1)", Faker.Lorem.Sentence());
        await transaction.CommitAsync();

        await Assert.That(await waiting).IsNull();
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(4));
        await Assert.That(await ScalarAsync(autocommit, "SELECT count(*) FROM notes")).IsEqualTo(3);
    }

    [Test]
    public async Task Rows_undone_by_rollback_to_a_savepoint_are_not_billed()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");
        var before = await WritesAsync(db);

        await db.OkAsync(
            Stmt("SAVEPOINT s"), Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()), Stmt("ROLLBACK TO s"),
            Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()));

        await Assert.That(await WritesAsync(db) - before).IsEqualTo(1);
        await Assert.That(await db.CountAsync("notes")).IsEqualTo(1);
    }

    [Test]
    public async Task A_trigger_body_with_an_end_column_and_nested_case_runs_as_one_statement()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE spans (id INTEGER PRIMARY KEY, end INTEGER, kind TEXT)", "CREATE TABLE log (label TEXT)");

        await db.OkAsync("""
            CREATE TRIGGER close_span AFTER INSERT ON spans BEGIN
                UPDATE spans SET end = 2 WHERE id = new.id;
                INSERT INTO log VALUES (CASE WHEN new.kind = 'a' THEN CASE WHEN new.id > 0 THEN 'positive' ELSE 'other' END ELSE 'b' END);
            END
            """);
        await db.OkAsync("INSERT INTO spans (id, kind) VALUES (1, 'a')");
        var span = await db.OkAsync("SELECT end FROM spans WHERE id = 1", "SELECT label FROM log");

        await Assert.That(span[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(2);
        await Assert.That(span[1].GetProperty("rows")[0][0].GetString()).IsEqualTo("positive");
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
        var racing = await db.Postgres.OpenConnectionAsync();
        try
        {
            for (var i = 0; i < 200; i++)
            {
                if (racing.State != System.Data.ConnectionState.Open)
                {
                    await racing.DisposeAsync();
                    try
                    {
                        racing = await db.Postgres.OpenConnectionAsync();
                    }
                    catch (PostgresException error) when (error.SqlState == "53300")
                    {
                        racing = db.Postgres.CreateConnection();
                        continue;
                    }
                }

                using var cancel = new CancellationTokenSource(TimeSpan.FromTicks(Faker.Random.Long(0, 30_000)));
                await using var command = new NpgsqlCommand(Short, racing);
                try
                {
                    _ = await command.ExecuteScalarAsync(cancel.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (PostgresException error) when (error.SqlState is "57014" or "53300")
                {
                }
            }
        }
        finally
        {
            await racing.DisposeAsync();
        }

        var answers = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                try
                {
                    await using var plain = await db.Postgres.OpenConnectionAsync(token);
                    answers.Add(await ScalarAsync(plain, "SELECT 1"));
                    return true;
                }
                catch (PostgresException error) when (error.SqlState == "53300")
                {
                    return false;
                }
            }, timeout: TimeSpan.FromSeconds(30));
        }

        await Assert.That(answers.Count(answer => answer == 1)).IsEqualTo(50);
    }

    private static async Task<long> WritesAsync(SqlNativeDatabase db)
    {
        var meter = db.Api.Server.Services.GetRequiredService<UsageMeter>();
        var tenant = new TenantContext(db.Database.Organization, PlanTier.Hobby, db.Database.Project, db.Database.Id);
        long writes = -1;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            await meter.FlushAsync(token);
            var window = new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
            if (await meter.GetAsync(tenant, window, token) is not UsageCounts counts || meter.PendingDatabases != 0)
            {
                return false;
            }

            writes = counts.Lifetime.Writes;
            return true;
        }, timeout: TimeSpan.FromSeconds(30));
        return writes;
    }
}
