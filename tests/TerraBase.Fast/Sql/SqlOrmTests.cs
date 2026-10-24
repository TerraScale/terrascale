using TerraBase.Fast.Documents;
using TerraBase.Features.Sql;

namespace TerraBase.Fast.Sql;

/// <summary>The statements Drizzle, Prisma and SQLAlchemy send, as captured from their libSQL drivers.</summary>
public sealed class SqlOrmTests
{
    [Test]
    public async Task Drizzle_migrations_run_with_backticks_a_serial_key_and_the_foreign_keys_pragma()
    {
        await using var api = await DocumentApi.StartAsync();

        var outcomes = await SqlHarness.RunAsync(api, canWrite: true,
            "CREATE TABLE IF NOT EXISTS \"drizzle_migrations\" (\n\t\t\tid SERIAL PRIMARY KEY,\n\t\t\thash text NOT NULL,\n\t\t\tcreated_at numeric\n\t\t)",
            "PRAGMA foreign_keys=off",
            "BEGIN DEFERRED",
            "CREATE TABLE `authors` (\n\t`id` integer PRIMARY KEY NOT NULL,\n\t`name` text NOT NULL,\n\t`email` text NOT NULL\n);\n",
            "CREATE UNIQUE INDEX `authors_email_unique` ON `authors` (`email`);",
            "CREATE TABLE `posts` (\n\t`id` integer PRIMARY KEY NOT NULL,\n\t`author_id` integer NOT NULL REFERENCES `authors`(`id`),\n\t`published` integer DEFAULT false NOT NULL\n);\n",
            "INSERT INTO \"drizzle_migrations\" (\"hash\", \"created_at\") VALUES('h', 1761300000000)",
            "COMMIT",
            "PRAGMA foreign_keys=on",
            "SELECT id, hash, created_at FROM \"drizzle_migrations\" ORDER BY created_at DESC LIMIT 1");

        await Assert.That(outcomes.Select(SqlHarness.Code)).IsEquivalentTo(new string?[10]);
        await Assert.That(outcomes[9] is SqlResult { Rows: [[{ Kind: SqlValueKind.Integer }, { Text: "h" }, _]] }).IsTrue();
    }

    [Test]
    public async Task Prisma_writes_with_main_qualified_names_return_their_rows()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE \"authors\" (\"id\" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, \"name\" TEXT NOT NULL, \"email\" TEXT NOT NULL)",
            "CREATE UNIQUE INDEX \"authors_email_key\" ON \"authors\"(\"email\")");

        var results = await SqlApi.OkAsync(api,
            "INSERT INTO `main`.`authors` (`name`, `email`) VALUES ('Ada', 'ada@example.com') RETURNING `id` AS `id`",
            "INSERT INTO `main`.`authors` (`id`, `name`, `email`) VALUES (7, 'Grace', 'grace@example.com') RETURNING *",
            "UPDATE `main`.`authors` SET `name` = 'Grace H.' WHERE (`main`.`authors`.`id` IN (7) AND 1=1) RETURNING `id` AS `id`, `name` AS `name`, `main`.`authors`.`email`",
            "SELECT `main`.`authors`.`name` FROM `main`.`authors` WHERE `main`.`authors`.`id` = 7 LIMIT 1 OFFSET 0",
            "DELETE FROM `main`.`authors` WHERE (`main`.`authors`.`email` = 'grace@example.com' AND 1=1) RETURNING `id` AS `id`, `name` AS `name`",
            "DELETE FROM authors WHERE id = 8 RETURNING id",
            "DELETE FROM `main`.`authors` WHERE (`main`.`authors`.`id` IN (8) AND EXISTS(SELECT `t0`.`id` FROM `main`.`authors` AS `t0` WHERE (`t0`.`name` = 'x' AND (`main`.`authors`.`id`) = (`t0`.`id`))))",
            "SELECT COUNT(*) AS `_count$_all` FROM (SELECT `main`.`authors`.`id` FROM `main`.`authors` WHERE 1=1 LIMIT 10 OFFSET 0) AS `sub`");

        await Assert.That(results[0].GetProperty("columns").GetRawText()).IsEqualTo("""[{"name":"id","decltype":"INTEGER"}]""");
        await Assert.That(results[0].GetProperty("rows")[0][0].GetInt64()).IsGreaterThan(0);
        await Assert.That(results[1].GetProperty("rows").GetRawText()).IsEqualTo("""[[7,"Grace","grace@example.com"]]""");
        await Assert.That(results[2].GetProperty("rows").GetRawText()).IsEqualTo("""[[7,"Grace H.","grace@example.com"]]""");
        await Assert.That(results[2].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(results[3].GetProperty("rows").GetRawText()).IsEqualTo("""[["Grace H."]]""");
        await Assert.That(results[4].GetProperty("rows").GetRawText()).IsEqualTo("""[[7,"Grace H."]]""");
        await Assert.That(results[5].GetProperty("rows").GetRawText()).IsEqualTo("[]");
        await Assert.That(results[6].GetProperty("rows_affected").GetInt64()).IsEqualTo(0);
        await Assert.That(results[7].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }

    [Test]
    public async Task Returning_lists_only_columns_of_the_written_table_and_describes_them_without_writing()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)");
        await using var session = SqlHarness.Open(api, canWrite: true);

        var described = await session.DescribeAsync(SqlHarness.Command("INSERT INTO t (v) VALUES (?) RETURNING id, v AS value"), CancellationToken.None);
        var unknown = await SqlHarness.RunAsync(api, "INSERT INTO t (v) VALUES ('x') RETURNING nope");
        var expression = await SqlHarness.RunAsync(api, "UPDATE t SET v = 'y' RETURNING length(v)");
        var other = await SqlHarness.RunAsync(api, "DELETE FROM t RETURNING u.id");
        var rows = await SqlApi.OkAsync(api, "SELECT count(*) FROM t");

        await Assert.That(described is SqlDescription { IsReadOnly: false } description
            && description.Columns.Select(column => column.Name).SequenceEqual(["id", "value"])).IsTrue();
        await Assert.That(SqlHarness.Code(unknown)).IsEqualTo("SQLITE_ERROR");
        await Assert.That(SqlHarness.Code(expression)).IsEqualTo("SQLITE_ERROR");
        await Assert.That(SqlHarness.Code(other)).IsEqualTo("SQLITE_ERROR");
        await Assert.That(rows[0].GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
    }

    [Test]
    public async Task SqlAlchemy_pragmas_answer_like_sqlite()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE authors (id INTEGER NOT NULL, name VARCHAR(100) NOT NULL DEFAULT 'it''s', score REAL DEFAULT 1.5, PRIMARY KEY (id))");

        var results = await SqlApi.OkAsync(api,
            "PRAGMA read_uncommitted",
            "PRAGMA main.table_info(\"authors\")",
            "PRAGMA temp.table_info(\"authors\")",
            "PRAGMA table_info(missing)");
        var reader = await SqlHarness.RunAsync(api, canWrite: false, "PRAGMA table_info('authors')");

        await Assert.That(results[0].GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
        await Assert.That(results[1].GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("name").GetString()!))
            .IsEquivalentTo(["cid", "name", "type", "notnull", "dflt_value", "pk"]);
        await Assert.That(results[1].GetProperty("rows").GetRawText().Replace("\\u0027", "'", StringComparison.Ordinal)).IsEqualTo(
            """[[0,"id","INTEGER",1,null,1],[1,"name","VARCHAR(100)",1,"'it''s'",0],[2,"score","REAL",0,"1.5",0]]""");
        await Assert.That(results[2].GetProperty("rows").GetRawText()).IsEqualTo("[]");
        await Assert.That(results[3].GetProperty("rows").GetRawText()).IsEqualTo("[]");
        await Assert.That(reader[0] is SqlResult { Rows.Count: 3 }).IsTrue();
    }
}
