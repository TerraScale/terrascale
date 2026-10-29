using Bogus;
using Npgsql;
using NpgsqlTypes;
using TerraBase.Testing;
using static TerraBase.Fast.Sql.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlNativePostgresTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Rows_are_created_read_updated_and_deleted_with_parameters_and_json_columns()
    {
        await using var db = await StartAsync();
        await using var connection = await db.Postgres.OpenConnectionAsync();
        var (name, city, renamed) = (Faker.Name.FullName(), Faker.Address.City(), Faker.Name.FullName());
        var age = Faker.Random.Int(18, 90);

        await PostgresSql.ExecuteAsync(connection, "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, profile TEXT)");
        var inserted = await PostgresSql.ExecuteAsync(connection, "INSERT INTO people (name, profile) VALUES ($1, $2)", name, $$"""{"city":"{{city}}","age":{{age}}}""");
        var read = await ReadAsync(connection, "SELECT id, name, json_extract(profile, '$.city'), profile ->> '$.age' FROM people WHERE name = $1", name);
        var id = Convert.ToInt64(read[0][0], System.Globalization.CultureInfo.InvariantCulture);
        var updated = await PostgresSql.ExecuteAsync(connection, "UPDATE people SET name = $1 WHERE id = $2", renamed, id);
        var after = await ReadAsync(connection, "SELECT name FROM people WHERE json_extract(profile, '$.city') = $1", city);
        var deleted = await PostgresSql.ExecuteAsync(connection, "DELETE FROM people WHERE id = $1", id);

        await Assert.That(inserted).IsEqualTo(1);
        await Assert.That(read[0][1]).IsEqualTo(name);
        await Assert.That(read[0][2]).IsEqualTo(city);
        await Assert.That(Convert.ToInt64(read[0][3], System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(age);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(after[0][0]).IsEqualTo(renamed);
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await ScalarAsync(connection, "SELECT count(*) FROM people")).IsEqualTo(0);
    }

    [Test]
    public async Task Schema_statements_pass_through_and_queries_use_their_indexes()
    {
        await using var db = await StartAsync();
        await using var connection = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(connection, "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");

        await PostgresSql.ExecuteAsync(connection, "ALTER TABLE people ADD COLUMN city TEXT");
        await PostgresSql.ExecuteAsync(connection, "INSERT INTO people (name, city) VALUES ($1, $2)", Faker.Name.FullName(), Faker.Address.City());
        await PostgresSql.ExecuteAsync(connection, "CREATE INDEX people_city ON people (city)");
        var plan = await ReadAsync(connection, "EXPLAIN QUERY PLAN SELECT name FROM people WHERE city = 'x'");
        await PostgresSql.ExecuteAsync(connection, "DROP TABLE people");
        var dropped = await StateAsync(connection, "SELECT * FROM people");

        await Assert.That(plan.Any(row => Convert.ToString(row[^1], System.Globalization.CultureInfo.InvariantCulture)!.Contains("people_city", StringComparison.Ordinal))).IsTrue();
        await Assert.That(dropped).IsEqualTo("42P01");
    }

    [Test]
    public async Task Constraints_answer_their_sqlstates_and_foreign_keys_cascade()
    {
        await using var db = await StartAsync();
        await using var connection = await db.Postgres.OpenConnectionAsync();
        await PostgresSql.ExecuteAsync(connection, "CREATE TABLE teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await PostgresSql.ExecuteAsync(connection,
            "CREATE TABLE players (id INTEGER PRIMARY KEY, team INTEGER NOT NULL REFERENCES teams (id) ON DELETE CASCADE, email TEXT UNIQUE, goals INTEGER CHECK (goals >= 0))");
        await PostgresSql.ExecuteAsync(connection, "INSERT INTO teams VALUES (1, 'core')");
        var email = Faker.Internet.Email();
        await PostgresSql.ExecuteAsync(connection, "INSERT INTO players VALUES (1, 1, $1, 3)", email);

        var primaryKey = await StateAsync(connection, "INSERT INTO players VALUES (1, 1, $1, 0)", Faker.Internet.Email());
        var unique = await StateAsync(connection, "INSERT INTO players VALUES (2, 1, $1, 0)", email);
        var notNull = await StateAsync(connection, "INSERT INTO players (id, email, goals) VALUES (2, $1, 0)", Faker.Internet.Email());
        var check = await StateAsync(connection, "INSERT INTO players VALUES (2, 1, $1, -1)", Faker.Internet.Email());
        var orphan = await StateAsync(connection, "INSERT INTO players VALUES (2, 99, $1, 0)", Faker.Internet.Email());
        await PostgresSql.ExecuteAsync(connection, "DELETE FROM teams WHERE id = 1");

        await Assert.That(primaryKey).IsEqualTo("23505");
        await Assert.That(unique).IsEqualTo("23505");
        await Assert.That(notNull).IsEqualTo("23502");
        await Assert.That(check).IsEqualTo("23514");
        await Assert.That(orphan).IsEqualTo("23503");
        await Assert.That(await ScalarAsync(connection, "SELECT count(*) FROM players")).IsEqualTo(0);
    }

    [Test]
    public async Task Bytea_parameters_round_trip_as_blobs()
    {
        await using var db = await StartAsync();
        await using var connection = await db.Postgres.OpenConnectionAsync();
        var bytes = Faker.Random.Bytes(Faker.Random.Int(1, 4096));
        await PostgresSql.ExecuteAsync(connection, "CREATE TABLE files (id INTEGER PRIMARY KEY, data BLOB NOT NULL)");

        await using (var insert = new NpgsqlCommand("INSERT INTO files (data) VALUES ($1)", connection))
        {
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = bytes });
            _ = await insert.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand("SELECT data, length(data) FROM files", connection);
        await using var reader = await select.ExecuteReaderAsync();
        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.GetFieldValue<byte[]>(0)).IsEquivalentTo(bytes);
        await Assert.That(Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(bytes.Length);
    }

    [Test]
    public async Task Read_only_keys_and_engine_tables_are_refused()
    {
        await using var db = await StartAsync();
        await using (var writer = await db.Postgres.OpenConnectionAsync())
        {
            await PostgresSql.ExecuteAsync(writer, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)");
        }

        await using var reader = await db.ReadOnlyPostgres.OpenConnectionAsync();
        var write = await StateAsync(reader, "INSERT INTO notes (body) VALUES ($1)", Faker.Lorem.Word());
        var state = await StateAsync(reader, "SELECT * FROM _tb_state");
        var attach = await StateAsync(reader, "ATTACH DATABASE 'other.db' AS other");

        await Assert.That(write).IsEqualTo("42501");
        await Assert.That(state).IsNotNull();
        await Assert.That(attach).IsNotNull();
        await Assert.That(await ScalarAsync(reader, "SELECT count(*) FROM notes")).IsEqualTo(0);
    }
}
