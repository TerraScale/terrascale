using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlConflictTests
{
    [Test]
    [Arguments("INSERT OR IGNORE", "")]
    [Arguments("INSERT", "ON CONFLICT DO NOTHING")]
    [Arguments("INSERT", "ON CONFLICT(email) DO NOTHING")]
    public async Task Ignored_unique_rows_do_not_fault_the_transaction_or_appear_in_returning(string insert, string conflict)
    {
        await using var api = await DocumentApi.StartAsync();
        var results = await SqlApi.OkAsync(api,
            "CREATE TABLE t (id INTEGER PRIMARY KEY, email TEXT UNIQUE, label TEXT NOT NULL)",
            "INSERT INTO t VALUES (1, 'a', 'original')",
            $"{insert} INTO t VALUES (2, 'a', 'skip'), (3, 'b', 'keep'), (4, 'b', 'skip'), (5, NULL, 'keep'), (6, NULL, 'keep') {conflict} RETURNING id",
            "SELECT id, email, label FROM t ORDER BY id");

        await Assert.That(results[2].GetProperty("rows_affected").GetInt64()).IsEqualTo(3);
        await Assert.That(results[2].GetProperty("last_insert_rowid").GetInt64()).IsEqualTo(6);
        await Assert.That(results[2].GetProperty("rows").GetRawText()).IsEqualTo("[[3],[5],[6]]");
        await Assert.That(results[3].GetProperty("rows").GetRawText())
            .IsEqualTo("""[[1,"a","original"],[3,"b","keep"],[5,null,"keep"],[6,null,"keep"]]""");
    }

    [Test]
    public async Task Ignore_skips_not_null_but_upsert_does_not_and_unmatched_targets_never_ignore_a_violation()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, email TEXT UNIQUE, label TEXT NOT NULL)", "INSERT INTO t VALUES (1, 'a', 'original')");
        var ignored = await SqlApi.OkAsync(api, "INSERT OR IGNORE INTO t VALUES (2, 'b', NULL), (3, 'c', 'kept') RETURNING id");
        await Assert.That(ignored[0].GetProperty("rows").GetRawText()).IsEqualTo("[[3]]");
        foreach (var (sql, code) in new[]
        {
            ("INSERT INTO t VALUES (2, 'a', NULL) ON CONFLICT DO NOTHING", "SQLITE_CONSTRAINT_NOTNULL"),
            ("INSERT INTO t VALUES (1, 'b', 'x') ON CONFLICT(email) DO NOTHING", "SQLITE_CONSTRAINT_PRIMARYKEY"),
            ("INSERT INTO t VALUES (2, 'a', 'x') ON CONFLICT(id) DO NOTHING", "SQLITE_CONSTRAINT_UNIQUE"),
            ("INSERT INTO t VALUES (1, 'a', 'x') ON CONFLICT(label) DO NOTHING", "SQLITE_ERROR"),
            ("INSERT INTO t VALUES (1, 'a', 'x') ON CONFLICT(id,id) DO NOTHING", "SQLITE_ERROR"),
            ("INSERT OR FAIL INTO t VALUES (9, 'z', 'x')", "SQLITE_ERROR"),
            ("INSERT OR ROLLBACK INTO t VALUES (9, 'z', 'x')", "SQLITE_ERROR"),
            ("INSERT OR REPLACE INTO t VALUES (2, 'a', NULL)", "SQLITE_CONSTRAINT_NOTNULL"),
        })
        {
            await Assert.That(SqlApi.Error(await SqlApi.ExecuteAsync(api, sql)).Sqlite).IsEqualTo(code);
        }

        var matched = await SqlApi.OkAsync(api, "INSERT INTO t VALUES (1, 'a', 'skip') ON CONFLICT(email) DO NOTHING RETURNING id", "SELECT id FROM t ORDER BY id");
        await Assert.That(matched[0].GetProperty("rows_affected").GetInt64()).IsEqualTo(0);
        await Assert.That(matched[0].GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await Assert.That(matched[1].GetProperty("rows").GetRawText()).IsEqualTo("[[1],[3]]");
    }

    [Test]
    public async Task Composite_targets_and_created_unique_indexes_use_the_declared_key()
    {
        await using var api = await DocumentApi.StartAsync();
        var results = await SqlApi.OkAsync(api,
            "CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT, b INTEGER, UNIQUE (a,b))",
            "CREATE UNIQUE INDEX pair_idx ON t (b,a)",
            "INSERT INTO t VALUES (1, 'x', 2)",
            "INSERT INTO t VALUES (2, 'x', 2) ON CONFLICT(B,A) DO NOTHING RETURNING id",
            "SELECT id FROM t",
            "CREATE TABLE c (a TEXT, b INTEGER, PRIMARY KEY (a,b))",
            "INSERT INTO c VALUES ('x', 2)",
            "INSERT INTO c VALUES ('x', 2) ON CONFLICT(b,a) DO NOTHING RETURNING a");
        await Assert.That(results[3].GetProperty("rows_affected").GetInt64()).IsEqualTo(0);
        await Assert.That(results[4].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
        await Assert.That(results[7].GetProperty("rows_affected").GetInt64()).IsEqualTo(0);
        await SqlApi.OkAsync(api, "CREATE TABLE identity_key (id TEXT PRIMARY KEY, email TEXT, UNIQUE (id,email))");
        var elided = await SqlApi.ExecuteAsync(api, "INSERT INTO identity_key VALUES ('a', 'x') ON CONFLICT(id,email) DO NOTHING");
        await Assert.That(SqlApi.Error(elided).Sqlite).IsEqualTo("SQLITE_ERROR");
    }

    [Test]
    [Arguments("REPLACE")]
    [Arguments("INSERT OR REPLACE")]
    public async Task Replace_removes_every_conflicting_row_and_uses_not_null_defaults_atomically(string insert)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE t (id INTEGER PRIMARY KEY, email TEXT UNIQUE, handle TEXT UNIQUE, label TEXT NOT NULL DEFAULT 'fresh')",
            "INSERT INTO t VALUES (1, 'a', 'x', 'old'), (2, 'b', 'y', 'old')");
        var failed = await SqlApi.ExecuteAsync(api, $"{insert} INTO t VALUES (3, 'a', 'y', NULL)", "INSERT INTO t VALUES (4, 'c', 'z', NULL)");
        await Assert.That(SqlApi.Error(failed).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_NOTNULL");
        var before = await SqlApi.OkAsync(api, "SELECT id FROM t ORDER BY id");
        await Assert.That(before[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1],[2]]");

        var results = await SqlApi.OkAsync(api, $"{insert} INTO t VALUES (3, 'a', 'y', NULL) RETURNING id, label", "SELECT id, email, handle, label FROM t");
        await Assert.That(results[0].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(results[0].GetProperty("last_insert_rowid").GetInt64()).IsEqualTo(3);
        await Assert.That(results[0].GetProperty("rows").GetRawText()).IsEqualTo("""[[3,"fresh"]]""");
        await Assert.That(results[1].GetProperty("rows").GetRawText()).IsEqualTo("""[[3,"a","y","fresh"]]""");
        await SqlApi.OkAsync(api, $"{insert} INTO t VALUES (3, 'a', 'y', 'same key')");
    }

}
