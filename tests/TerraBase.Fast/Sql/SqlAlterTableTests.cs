using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAlterTableTests
{
    [Test]
    public async Task Added_columns_are_nullable_last_in_the_catalog_and_writable()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT, UNIQUE(value))", "INSERT INTO t VALUES (1, 'old')");
        await SqlApi.OkAsync(api, "ALTER TABLE t ADD COLUMN extra TEXT DEFAULT NULL");
        var before = await SqlApi.OkAsync(api, "SELECT * FROM t", "PRAGMA table_info(t)", "SELECT sql FROM sqlite_master WHERE name = 't'");
        await Assert.That(before[0].GetProperty("rows")[0].GetRawText()).IsEqualTo("""[1,"old",null]""");
        await Assert.That(before[0].GetProperty("columns")[2].GetProperty("name").GetString()).IsEqualTo("extra");
        await Assert.That(before[1].GetProperty("rows")[2].GetRawText()).IsEqualTo("""[2,"extra","TEXT",0,null,0]""");
        var catalog = before[2].GetProperty("rows")[0][0].GetString()!;
        await Assert.That(catalog).Contains("extra TEXT");
        await Assert.That(catalog).Contains("UNIQUE (value)");

        var arity = await SqlApi.ExecuteAsync(api, "INSERT INTO t VALUES (2, 'new')");
        await Assert.That(arity.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await SqlApi.OkAsync(api, "INSERT INTO t VALUES (2, 'new', 'inserted')", "UPDATE t SET extra = 'updated' WHERE id = 1");
        var after = await SqlApi.OkAsync(api, "SELECT extra FROM t ORDER BY id");
        await Assert.That(after[0].GetProperty("rows").GetRawText()).IsEqualTo("""[["updated"],["inserted"]]""");
    }

    [Test]
    public async Task Create_alter_and_insert_share_a_transaction_and_failed_batches_roll_back_ddl()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY)", "ALTER TABLE t ADD extra TEXT NULL",
            "INSERT INTO t VALUES (1, 'kept')");
        var kept = await SqlApi.OkAsync(api, "SELECT extra FROM t");
        await Assert.That(kept[0].GetProperty("rows")[0][0].GetString()).IsEqualTo("kept");

        var failed = await SqlApi.ExecuteAsync(api, "ALTER TABLE t ADD discarded TEXT", "INSERT INTO missing VALUES (1)");
        await Assert.That(SqlApi.Error(failed)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 1));
        var columns = await SqlApi.OkAsync(api, "PRAGMA table_info(t)");
        await Assert.That(columns[0].GetProperty("rows").GetArrayLength()).IsEqualTo(2);
        using var reader = await api.ClientWithScopesAsync("documents:read");
        var denied = await SqlApi.ExecuteAsync(api, reader, "ALTER TABLE t ADD denied TEXT");
        await Assert.That(SqlApi.Error(denied)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope", "SQLITE_AUTH", 0));
    }

    [Test]
    public async Task Unsupported_alterations_are_refused_without_changing_the_definition()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY)");
        string[] refused =
        [
            "ALTER TABLE t ADD extra TEXT NOT NULL", "ALTER TABLE t ADD extra TEXT DEFAULT 'x'",
            "ALTER TABLE t ADD extra TEXT PRIMARY KEY", "ALTER TABLE t ADD extra TEXT UNIQUE",
            "ALTER TABLE t ADD extra TEXT REFERENCES t(id)", "ALTER TABLE t ADD extra TEXT CHECK(extra <> '')",
            "ALTER TABLE t ADD extra TEXT COLLATE NOCASE", "ALTER TABLE t ADD extra SERIAL",
            "ALTER TABLE t ADD ID TEXT", "ALTER TABLE t DROP COLUMN id", "ALTER TABLE t RENAME TO u",
            "ALTER TABLE t ADD a TEXT, ADD b TEXT", "ALTER TABLE t ADD IF NOT EXISTS extra TEXT",
            "ALTER TABLE t ADD COLUMN IF NOT EXISTS extra TEXT", "ALTER TABLE IF EXISTS t ADD extra TEXT",
            "ALTER TABLE missing ADD extra TEXT", "ALTER TABLE items ADD extra TEXT",
        ];
        foreach (var sql in refused)
        {
            var response = await SqlApi.ExecuteAsync(api, sql);
            await Assert.That(SqlApi.Error(response)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 0)).Because(sql);
        }

        var columns = await SqlApi.OkAsync(api, "PRAGMA table_info(t)");
        await Assert.That(columns[0].GetProperty("rows").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Sqlite_quoting_omitted_types_and_added_column_affinity_keep_their_meaning()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY)", "INSERT INTO t VALUES (1)",
            "ALTER TABLE [t] ADD [raw]", "ALTER TABLE t ADD `number` INTEGER", "ALTER TABLE t ADD \"Extra\" TEXT DEFAULT NULL");
        await SqlApi.OkAsync(api, "UPDATE t SET raw = '12', number = '12'");
        var values = await SqlApi.OkAsync(api, "SELECT raw, number, Extra FROM t", "PRAGMA table_info(t)");
        await Assert.That(values[0].GetProperty("rows")[0].GetRawText()).IsEqualTo("""["12",12,null]""");
        await Assert.That(values[1].GetProperty("rows")[1][2].GetString()).IsEqualTo("");
        var invalid = await SqlApi.ExecuteAsync(api, "ALTER TABLE t ADD bad NOT NULL");
        await Assert.That(invalid.Status).IsEqualTo(HttpStatusCode.BadRequest);
        var after = await SqlApi.OkAsync(api, "PRAGMA table_info(t)");
        await Assert.That(after[0].GetProperty("rows").GetArrayLength()).IsEqualTo(4);
    }

    [Test]
    public async Task The_column_limit_is_enforced_and_an_existing_document_field_becomes_visible()
    {
        await using var api = await DocumentApi.StartAsync();
        var columns = string.Join(", ", Enumerable.Range(0, 255).Select(i => $"c{i} TEXT"));
        await SqlApi.OkAsync(api, $"CREATE TABLE wide ({columns})", "ALTER TABLE wide ADD last TEXT");
        var tooWide = await SqlApi.ExecuteAsync(api, "ALTER TABLE wide ADD excess TEXT");
        await Assert.That(tooWide.Status).IsEqualTo(HttpStatusCode.BadRequest);
        var catalog = await SqlApi.OkAsync(api, "PRAGMA table_info(wide)");
        await Assert.That(catalog[0].GetProperty("rows").GetArrayLength()).IsEqualTo(256);

        await SqlApi.OkAsync(api, "CREATE TABLE t (id TEXT PRIMARY KEY)");
        var id = TerraBase.Primitives.EntityIds.New().ToString();
        var written = await api.SendAsync(HttpMethod.Put, $"{api.Collections}/t/documents/by-key?id={id}", new { extra = "existing" });
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        await SqlApi.OkAsync(api, "ALTER TABLE t ADD extra TEXT");
        var values = await SqlApi.OkAsync(api, "SELECT extra FROM t");
        await Assert.That(values[0].GetProperty("rows")[0][0].GetString()).IsEqualTo("existing");
    }
}
