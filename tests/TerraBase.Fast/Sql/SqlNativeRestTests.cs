using System.Net;
using Bogus;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlNativeRestTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Rows_are_created_read_updated_and_deleted_with_parameters_and_json_columns()
    {
        await using var db = await StartAsync();
        var (name, city, renamed) = (Faker.Name.FullName(), Faker.Address.City(), Faker.Name.FullName());
        var age = Faker.Random.Int(18, 90);

        await db.OkAsync("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, profile TEXT)");
        var inserted = await db.OkAsync(Stmt("INSERT INTO people (name, profile) VALUES (?, ?)", name, $$"""{"city":"{{city}}","age":{{age}}}"""));
        var id = inserted[0].GetProperty("last_insert_rowid").GetInt64();
        var read = await db.OkAsync(Stmt("SELECT name, json_extract(profile, '$.city'), profile ->> '$.age' FROM people WHERE id = ?", id));
        var updated = await db.OkAsync(Named("UPDATE people SET name = :name WHERE id = :id", new() { ["name"] = renamed, ["id"] = id }));
        var after = await db.OkAsync(Stmt("SELECT name FROM people WHERE json_extract(profile, '$.age') = ?", age));
        var deleted = await db.OkAsync(Stmt("DELETE FROM people WHERE id = ?1", id));

        await Assert.That(inserted[0].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(read[0].GetProperty("rows")[0][0].GetString()).IsEqualTo(name);
        await Assert.That(read[0].GetProperty("rows")[0][1].GetString()).IsEqualTo(city);
        await Assert.That(read[0].GetProperty("rows")[0][2].GetInt64()).IsEqualTo(age);
        await Assert.That(updated[0].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(after[0].GetProperty("rows")[0][0].GetString()).IsEqualTo(renamed);
        await Assert.That(deleted[0].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(await db.CountAsync("people")).IsEqualTo(0);
    }

    [Test]
    public async Task Schema_statements_pass_through_and_queries_use_their_indexes()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await db.OkAsync(Stmt("INSERT INTO people (name) VALUES (?)", Faker.Name.FullName()));

        await db.OkAsync("ALTER TABLE people ADD COLUMN city TEXT");
        await db.OkAsync(Stmt("UPDATE people SET city = ?", Faker.Address.City()));
        await db.OkAsync("CREATE INDEX people_city ON people (city)");
        var plan = await db.OkAsync(Stmt("EXPLAIN QUERY PLAN SELECT name FROM people WHERE city = ?", "x"));
        var columns = await db.OkAsync("PRAGMA table_info(people)");
        await db.OkAsync("DROP TABLE people");
        var dropped = await db.RefusedAsync("SELECT * FROM people");

        await Assert.That(plan[0].GetProperty("rows").EnumerateArray().Any(row => row[3].GetString()!.Contains("people_city", StringComparison.Ordinal))).IsTrue();
        await Assert.That(columns[0].GetProperty("rows").EnumerateArray().Select(row => row[1].GetString()!).ToList()).IsEquivalentTo(["id", "name", "city"]);
        await Assert.That(dropped.Code).IsEqualTo("SQLITE_ERROR");
    }

    [Test]
    public async Task Constraints_answer_their_sqlite_codes_and_foreign_keys_cascade()
    {
        await using var db = await StartAsync();
        await db.OkAsync(
            "CREATE TABLE teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE players (id INTEGER PRIMARY KEY, team INTEGER NOT NULL REFERENCES teams (id) ON DELETE CASCADE, email TEXT UNIQUE, goals INTEGER CHECK (goals >= 0))",
            "INSERT INTO teams VALUES (1, 'core')");
        var email = Faker.Internet.Email();
        await db.OkAsync(Stmt("INSERT INTO players VALUES (1, 1, ?, 3)", email));

        var primaryKey = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (1, 1, ?, 0)", Faker.Internet.Email()));
        var unique = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, 1, ?, 0)", email));
        var notNull = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, NULL, ?, 0)", Faker.Internet.Email()));
        var check = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, 1, ?, -1)", Faker.Internet.Email()));
        var orphan = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, 99, ?, 0)", Faker.Internet.Email()));
        var enforced = await db.OkAsync("PRAGMA foreign_keys");
        await db.OkAsync("DELETE FROM teams WHERE id = 1");

        await Assert.That(primaryKey.Code).IsEqualTo("SQLITE_CONSTRAINT_PRIMARYKEY");
        await Assert.That(unique.Code).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
        await Assert.That(notNull.Code).IsEqualTo("SQLITE_CONSTRAINT_NOTNULL");
        await Assert.That(check.Code).IsEqualTo("SQLITE_CONSTRAINT_CHECK");
        await Assert.That(orphan.Code).IsEqualTo("SQLITE_CONSTRAINT_FOREIGNKEY");
        await Assert.That(enforced[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
        await Assert.That(await db.CountAsync("players")).IsEqualTo(0);
    }

    [Test]
    public async Task A_request_is_one_transaction_and_a_failed_statement_rolls_all_of_it_back()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

        var failed = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()), Stmt("INSERT INTO notes (body) VALUES (NULL)"));
        var committed = await db.OkAsync(
            Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()), Stmt("SAVEPOINT s"), Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()),
            Stmt("ROLLBACK TO s"), Stmt("SELECT count(*) FROM notes"));
        var control = await db.RefusedAsync("BEGIN");

        await Assert.That((failed.Code, failed.Statement)).IsEqualTo(("SQLITE_CONSTRAINT_NOTNULL", 1));
        await Assert.That(committed[4].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
        await Assert.That(await db.CountAsync("notes")).IsEqualTo(1);
        await Assert.That(control.Status).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Read_only_keys_read_but_cannot_write()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)", "INSERT INTO notes (body) VALUES ('kept')");

        var write = await db.RefusedAsync(db.Reader, Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Word()));
        var schema = await db.RefusedAsync(db.Reader, Stmt("DROP TABLE notes"));
        var (status, body) = await db.PostAsync(db.Reader, Stmt("SELECT body FROM notes"));

        await Assert.That((write.Status, write.Code)).IsEqualTo((HttpStatusCode.Forbidden, "SQLITE_AUTH"));
        await Assert.That(schema.Code).IsEqualTo("SQLITE_AUTH");
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("results")[0].GetProperty("rows")[0][0].GetString()).IsEqualTo("kept");
    }

    [Test]
    public async Task Engine_tables_attach_and_disabling_foreign_keys_are_refused()
    {
        await using var db = await StartAsync();

        var state = await db.RefusedAsync("SELECT * FROM _tb_state");
        var quoted = await db.RefusedAsync("SELECT * FROM \"_TB_STATE\"");
        var info = await db.RefusedAsync("PRAGMA table_info('_tb_state')");
        var catalog = await db.RefusedAsync("DELETE FROM collections");
        var attach = await db.RefusedAsync("ATTACH DATABASE 'other.db' AS other");
        var off = await db.RefusedAsync("PRAGMA foreign_keys = OFF");
        var unsupported = await db.RefusedAsync("PRAGMA writable_schema = 1");
        var schema = await db.OkAsync("SELECT count(*) FROM sqlite_schema");

        foreach (var refused in new[] { state, quoted, info, catalog, attach, off, unsupported })
        {
            await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.BadRequest, "SQLITE_ERROR"));
        }

        await Assert.That(schema[0].GetProperty("rows")[0][0].GetInt64()).IsGreaterThan(0);
    }

    [Test]
    public async Task Blobs_round_trip_as_base64()
    {
        await using var db = await StartAsync();
        var bytes = Faker.Random.Bytes(Faker.Random.Int(1, 4096));
        await db.OkAsync("CREATE TABLE files (id INTEGER PRIMARY KEY, data BLOB NOT NULL)");

        await db.OkAsync(Stmt("INSERT INTO files (data) VALUES (?)", Blob(bytes)));
        var read = await db.OkAsync("SELECT data, length(data), typeof(data) FROM files");

        var row = read[0].GetProperty("rows")[0];
        await Assert.That(Convert.FromBase64String(row[0].GetProperty("base64").GetString()!)).IsEquivalentTo(bytes);
        await Assert.That(row[1].GetInt64()).IsEqualTo(bytes.Length);
        await Assert.That(row[2].GetString()).IsEqualTo("blob");
    }
}
