using Bogus;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

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
