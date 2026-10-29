using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TerraBase.Features.Sql;
using TerraBase.Testing;
using static TerraBase.Fast.Sql.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlNativeTransactionTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_transaction_spans_round_trips_and_stays_invisible_to_other_connections_until_it_commits()
    {
        await using var db = await StartAsync();
        await using var writer = await db.Postgres.OpenConnectionAsync();
        await using var other = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(writer, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

        await using (var transaction = await writer.BeginTransactionAsync())
        {
            await PostgresSql.ExecuteAsync(writer, "INSERT INTO notes (body) VALUES ($1)", Faker.Lorem.Sentence());
            await PostgresSql.ExecuteAsync(writer, "INSERT INTO notes (body) VALUES ($1)", Faker.Lorem.Sentence());
            var inside = await ScalarAsync(writer, "SELECT count(*) FROM notes");
            var outside = await ScalarAsync(other, "SELECT count(*) FROM notes");
            await transaction.CommitAsync();

            await Assert.That(inside).IsEqualTo(2);
            await Assert.That(outside).IsEqualTo(0);
        }

        await Assert.That(await ScalarAsync(other, "SELECT count(*) FROM notes")).IsEqualTo(2);
    }

    [Test]
    public async Task Rollback_discards_and_a_failed_statement_rolls_back_to_its_savepoint_while_the_transaction_continues()
    {
        await using var db = await StartAsync();
        await using var connection = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(connection, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

        await using (var discarded = await connection.BeginTransactionAsync())
        {
            await PostgresSql.ExecuteAsync(connection, "INSERT INTO notes (id, body) VALUES (1, $1)", Faker.Lorem.Sentence());
            await discarded.RollbackAsync();
        }

        string? failed;
        await using (var kept = await connection.BeginTransactionAsync())
        {
            await PostgresSql.ExecuteAsync(connection, "INSERT INTO notes (id, body) VALUES (1, $1)", Faker.Lorem.Sentence());
            await PostgresSql.ExecuteAsync(connection, "SAVEPOINT before_batch");
            failed = await StateAsync(connection, "INSERT INTO notes (id, body) VALUES (2, 'two'), (1, 'again')");
            await PostgresSql.ExecuteAsync(connection, "ROLLBACK TO SAVEPOINT before_batch");
            await PostgresSql.ExecuteAsync(connection, "INSERT INTO notes (id, body) VALUES (3, $1)", Faker.Lorem.Sentence());
            await kept.CommitAsync();
        }

        var ids = await ReadAsync(connection, "SELECT id FROM notes ORDER BY id");

        await Assert.That(failed).IsEqualTo("23505");
        await Assert.That(ids.Select(row => Convert.ToInt64(row[0], System.Globalization.CultureInfo.InvariantCulture))).IsEquivalentTo([1L, 3L]);
    }

    [Test]
    public async Task An_idle_transaction_expires_after_five_seconds_releases_the_writer_and_its_commit_fails()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var db = await StartAsync(services => services.AddKeyedSingleton<TimeProvider>(SqlLimits.TransactionClock, clock));
        await using var idle = await db.Postgres.OpenConnectionAsync();
        await using var other = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(idle, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

        await using var transaction = await idle.BeginTransactionAsync();
        await PostgresSql.ExecuteAsync(idle, "INSERT INTO notes (id, body) VALUES (1, $1)", Faker.Lorem.Sentence());
        clock.Advance(SqlLimits.MaxTransactionTime + TimeSpan.FromSeconds(1));

        await TerraBaseTestServer.EventuallyAsync(async _ => await StateAsync(other, "INSERT OR REPLACE INTO notes (id, body) VALUES (2, $1)", Faker.Lorem.Sentence()) is null,
            TimeSpan.FromSeconds(10));
        var commit = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        var ids = await ReadAsync(other, "SELECT id FROM notes");

        await Assert.That(commit!.SqlState).IsEqualTo("57014");
        await Assert.That(ids.Select(row => Convert.ToInt64(row[0], System.Globalization.CultureInfo.InvariantCulture))).IsEquivalentTo([2L]);
    }
}
