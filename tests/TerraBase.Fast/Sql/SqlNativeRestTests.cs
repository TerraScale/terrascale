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
        await using var db = await StartSharedAsync();
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
        await using var db = await StartSharedAsync();
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
        await Assert.That(dropped.Code).IsEqualTo("undefined_table");
    }

    [Test]
    public async Task Constraints_answer_their_error_codes_and_foreign_keys_cascade()
    {
        await using var db = await StartSharedAsync();
        await db.OkAsync(
            "CREATE TABLE teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE players (id INTEGER PRIMARY KEY, team INTEGER NOT NULL REFERENCES teams (id) ON DELETE CASCADE, email TEXT UNIQUE, goals INTEGER CHECK (goals >= 0))",
            "INSERT INTO teams VALUES (1, 'core')");
        var email = Faker.Internet.Email();
        await db.OkAsync(Stmt("INSERT INTO players VALUES (1, 1, ?, 3)", email));

        var (primaryKeyStatus, primaryKeyBody) = await db.PostAsync(db.Writer, Stmt("INSERT INTO players VALUES (1, 1, ?, 0)", Faker.Internet.Email()));
        var primaryKey = primaryKeyBody.GetProperty("error");
        var unique = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, 1, ?, 0)", email));
        var notNull = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, NULL, ?, 0)", Faker.Internet.Email()));
        var check = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, 1, ?, -1)", Faker.Internet.Email()));
        var orphan = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO players VALUES (2, 99, ?, 0)", Faker.Internet.Email()));
        var enforced = await db.OkAsync("PRAGMA foreign_keys");
        await db.OkAsync("DELETE FROM teams WHERE id = 1");

        await Assert.That((primaryKeyStatus, primaryKey.GetProperty("code").GetString(), primaryKey.GetProperty("sqlstate").GetString()))
            .IsEqualTo((HttpStatusCode.BadRequest, "primary_key_violation", "23505"));
        await Assert.That(primaryKey.EnumerateObject().Select(property => property.Name).ToArray())
            .IsEquivalentTo(["code", "message", "statement", "sqlstate", "requestId", "docsUrl"]);
        await Assert.That(primaryKey.GetProperty("requestId").GetString()).IsNotNull();
        await Assert.That(primaryKey.GetProperty("docsUrl").GetString())
            .IsEqualTo("https://docs.terrascale.tech/guides/errors#primary-key-violation");
        await Assert.That(unique.Code).IsEqualTo("unique_violation");
        await Assert.That(notNull.Code).IsEqualTo("not_null_violation");
        await Assert.That(check.Code).IsEqualTo("check_violation");
        await Assert.That(orphan.Code).IsEqualTo("foreign_key_violation");
        await Assert.That(enforced[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
        await Assert.That(await db.CountAsync("players")).IsEqualTo(0);
    }

    [Test]
    public async Task Full_outer_join_answers_a_feature_refusal()
    {
        await using var db = await StartSharedAsync();
        await db.OkAsync(
            "CREATE TABLE teams (id INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE players (id INTEGER PRIMARY KEY, team INTEGER REFERENCES teams (id))");

        var (status, body) = await db.PostAsync(db.Reader, Stmt("SELECT * FROM teams FULL JOIN players ON players.team = teams.id"));
        var (spelled, spelledBody) = await db.PostAsync(db.Reader, Stmt("SELECT * FROM teams full outer join players on players.team = teams.id"));
        var word = await db.OkAsync("SELECT * FROM teams JOIN players ON players.team = teams.id WHERE teams.name <> 'FULL JOIN'");
        var rewritten = await db.RefusedAsync(db.Writer, Stmt("UPDATE players SET team = 1 WHERE team IN (SELECT team FROM teams FULL OUTER JOIN players USING (team))"));

        var error = body.GetProperty("error");
        await Assert.That((status, error.GetProperty("code").GetString(), error.GetProperty("sqlstate").GetString(), error.GetProperty("message").GetString()))
            .IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "0A000", "full outer join is not supported; use LEFT JOIN with UNION ALL"));
        await Assert.That((spelled, spelledBody.GetProperty("error").GetProperty("sqlstate").GetString())).IsEqualTo((HttpStatusCode.BadRequest, "0A000"));
        await Assert.That(word[0].GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await Assert.That(rewritten.Code).IsEqualTo("sql_error");
    }

    [Test]
    public async Task A_request_is_one_transaction_and_a_failed_statement_rolls_all_of_it_back()
    {
        await using var db = await StartSharedAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

        var failed = await db.RefusedAsync(db.Writer, Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()), Stmt("INSERT INTO notes (body) VALUES (NULL)"));
        var committed = await db.OkAsync(
            Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()), Stmt("SAVEPOINT s"), Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()),
            Stmt("ROLLBACK TO s"), Stmt("SELECT count(*) FROM notes"));
        var control = await db.RefusedAsync("BEGIN");

        await Assert.That((failed.Code, failed.Statement)).IsEqualTo(("not_null_violation", 1));
        await Assert.That(committed[4].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
        await Assert.That(await db.CountAsync("notes")).IsEqualTo(1);
        await Assert.That(control.Status).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task A_sql_read_key_reads_but_is_refused_on_a_write()
    {
        await using var db = await StartSharedAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)", "INSERT INTO notes (body) VALUES ('kept')");

        var write = await db.RefusedAsync(db.Reader, Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Word()));
        var schema = await db.RefusedAsync(db.Reader, Stmt("DROP TABLE notes"));
        var (status, body) = await db.PostAsync(db.Reader, Stmt("SELECT body FROM notes"));

        await Assert.That((write.Status, write.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        await Assert.That(schema.Code).IsEqualTo("forbidden_scope");
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("results")[0].GetProperty("rows")[0][0].GetString()).IsEqualTo("kept");
    }

    [Test]
    public async Task A_table_name_is_reusable_after_create_and_drop_in_one_batch_and_across_requests()
    {
        await using var db = await StartSharedAsync();

        var batch = await db.OkAsync(
            "CREATE TABLE reuse_t (id INTEGER PRIMARY KEY, v TEXT)",
            "INSERT INTO reuse_t VALUES (1, 'a')",
            "DROP TABLE reuse_t",
            "CREATE TABLE reuse_t (id INTEGER PRIMARY KEY, v TEXT)");
        await db.OkAsync("DROP TABLE reuse_t");
        await db.OkAsync("CREATE TABLE reuse_t (id INTEGER PRIMARY KEY)");

        await Assert.That(batch.Length).IsEqualTo(4);
        await Assert.That(batch[1].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(await db.CountAsync("reuse_t")).IsEqualTo(0);
    }

    [Test]
    public async Task Unfiltered_returning_writes_and_value_is_comparisons_answer_like_sqlite()
    {
        await using var db = await StartSharedAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

        var deleted = await db.OkAsync("INSERT INTO notes (body) VALUES ('one'), ('two')", "DELETE FROM notes RETURNING id, body");
        var updated = await db.OkAsync("INSERT INTO notes (body) VALUES ('three')", "UPDATE notes SET body = 'kept' RETURNING body");
        var compared = await db.OkAsync("SELECT 0 IS 0, 1 IS NOT 0, NULL IS NOT 1, 1 IS NULL, NULL IS NULL");

        await Assert.That(deleted[1].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
        await Assert.That(deleted[1].GetProperty("rows")[0][1].GetString()).IsEqualTo("one");
        await Assert.That(deleted[1].GetProperty("rows")[1][1].GetString()).IsEqualTo("two");
        await Assert.That(deleted[1].GetProperty("rows_affected").GetInt64()).IsEqualTo(2);
        await Assert.That(updated[1].GetProperty("rows")[0][0].GetString()).IsEqualTo("kept");
        var values = compared[0].GetProperty("rows")[0];
        await Assert.That(values[0].GetInt64()).IsEqualTo(1);
        await Assert.That(values[1].GetInt64()).IsEqualTo(1);
        await Assert.That(values[2].GetInt64()).IsEqualTo(1);
        await Assert.That(values[3].GetInt64()).IsEqualTo(0);
        await Assert.That(values[4].GetInt64()).IsEqualTo(1);
        await Assert.That(await db.CountAsync("notes")).IsEqualTo(1);
    }

    [Test]
    public async Task The_free_plan_lists_ids_and_deletes_large_rows_by_id()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE blobs (id TEXT PRIMARY KEY, body BLOB NOT NULL)");
        for (var i = 0; i < 24; i++)
        {
            await db.OkAsync(Stmt("INSERT INTO blobs (id, body) VALUES (?, randomblob(716800))", $"row-{i:D3}"));
        }

        var ids = await db.OkAsync("SELECT id FROM blobs");
        var deleted = await db.OkAsync(Stmt("DELETE FROM blobs WHERE id = ?", "row-010"));
        var after = await db.OkAsync("SELECT id FROM blobs");
        var remaining = await db.OkAsync(Stmt("SELECT id FROM blobs WHERE id = ?", "row-011"));
        var fullRow = await db.RefusedAsync("SELECT id, body FROM blobs");

        await Assert.That(ids[0].GetProperty("rows").GetArrayLength()).IsEqualTo(24);
        await Assert.That(deleted[0].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(after[0].GetProperty("rows").GetArrayLength()).IsEqualTo(23);
        await Assert.That(remaining[0].GetProperty("rows").GetArrayLength()).IsEqualTo(1);
        await Assert.That((fullRow.Status, fullRow.Code)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "too_large"));
    }

    [Test]
    public async Task Blobs_round_trip_as_base64()
    {
        await using var db = await StartSharedAsync();
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
