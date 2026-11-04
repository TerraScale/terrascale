using Bogus;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

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
}
