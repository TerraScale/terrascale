using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlSandboxTests
{
    private static readonly Faker Faker = new();

    private static readonly string[] Denied =
    [
        "ATTACH DATABASE 'other.db' AS other",
        "ATTACH 'file:/etc/passwd?mode=ro' AS leak",
        "DETACH DATABASE main",
        "VACUUM",
        "VACUUM INTO '/tmp/terrabase-escape.db'",
        "vacuum main into 'copy.db'",
        "ANALYZE",
        "EXPLAIN ATTACH DATABASE 'other.db' AS other",
        "EXPLAIN QUERY PLAN VACUUM INTO 'copy.db'",
        "SELECT load_extension('/tmp/evil.so')",
        "SELECT LOAD_EXTENSION('/tmp/evil.so', 'entry')",
        "SELECT readfile('/etc/passwd')",
        "SELECT writefile('/tmp/terrabase-escape', 'x')",
        "SELECT edit('x', 'vi')",
        "SELECT * FROM fsdir('/')",
        "SELECT * FROM sqlite_dbpage",
        "UPDATE sqlite_dbpage SET data = zeroblob(4096) WHERE pgno = 1",
        "SELECT * FROM btree_dump",
        "SELECT * FROM dbstat",
        "SELECT * FROM temp.sqlite_schema",
        "SELECT * FROM sqlite_temp_master",
        "CREATE VIRTUAL TABLE leak USING csv(filename = '/etc/passwd')",
        "CREATE TEMP TABLE scratch (id INTEGER)",
        "DELETE FROM sqlite_schema",
        "UPDATE sqlite_master SET sql = 'x' WHERE name = 'notes'",
        "INSERT INTO main.sqlite_schema VALUES ('table', 'x', 'x', 0, 'CREATE TABLE x (id)')",
        "DROP TABLE sqlite_schema",
        "CREATE TRIGGER spy AFTER INSERT ON notes BEGIN INSERT INTO notes (body) SELECT name FROM sqlite_schema; END",
        "PRAGMA writable_schema = 1",
        "PRAGMA journal_mode = DELETE",
        "PRAGMA journal_mode",
        "PRAGMA database_list",
        "PRAGMA cache_size = -100000",
        "PRAGMA mmap_size = 268435456",
        "PRAGMA foreign_keys = OFF",
        "PRAGMA temp.table_info(notes)",
        "PRAGMA table_info = notes",
        "CREATE TABLE _ts_meta (id INTEGER PRIMARY KEY)",
        "CREATE TABLE turso_cdc_shadow (id INTEGER PRIMARY KEY)",
        "CREATE TABLE \"_TB_shadow\" (id INTEGER PRIMARY KEY)",
        "ALTER TABLE notes RENAME TO _tb_notes",
        "CREATE INDEX notes_hidden ON _tb_state (id)",
    ];

    private static readonly string[] Internal =
    [
        "SELECT * FROM turso_cdc",
        "SELECT count(*) FROM turso_sync_cdc",
        "SELECT * FROM turso_cdc_version",
        "SELECT * FROM _tb_state",
        "SELECT * FROM \"_TB_STATE\"",
        "SELECT * FROM [_tb_feed]",
        "SELECT * FROM _ts_meta",
        "SELECT * FROM _collections",
        "SELECT * FROM collections",
        "SELECT * FROM commit_offset",
        "SELECT * FROM notes JOIN _tb_state ON 1 = 1",
        "WITH x AS (SELECT * FROM _indexes) SELECT * FROM x",
        "INSERT INTO _tb_state (collection) VALUES ('x')",
        "DELETE FROM turso_cdc",
        "DROP TABLE _tb_state",
        "PRAGMA table_info(turso_cdc)",
        "PRAGMA index_list('_tb_state')",
        "SELECT * FROM pragma_table_list",
        "SELECT * FROM pragma_table_info('_tb_state')",
    ];

    [Test]
    public async Task Every_escape_statement_is_refused_over_rest_with_sql_sandbox()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)");

        foreach (var sql in Denied)
        {
            var (status, body) = await db.PostAsync(db.Writer, Stmt(sql));
            var error = body.GetProperty("error");
            await Assert.That((status, error.GetProperty("code").GetString(), error.GetProperty("sqlite_code").GetString()))
                .IsEqualTo((HttpStatusCode.Forbidden, SqlSandboxCode, "SQLITE_AUTH")).Because(sql);
        }

        var reader = await db.RefusedAsync(db.Reader, Stmt("VACUUM INTO 'copy.db'"));
        await Assert.That((reader.Status, reader.Code)).IsEqualTo((HttpStatusCode.Forbidden, "SQLITE_AUTH"));
        await Assert.That(await db.CountAsync("notes")).IsEqualTo(0);
    }

    [Test]
    public async Task Internal_tables_do_not_exist_for_rest_queries()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)");

        foreach (var sql in Internal)
        {
            var (status, body) = await db.PostAsync(db.Writer, Stmt(sql));
            var error = body.GetProperty("error");
            await Assert.That((status, error.GetProperty("code").GetString())).IsEqualTo((HttpStatusCode.BadRequest, "sql_error")).Because(sql);
            await Assert.That(error.GetProperty("message").GetString()).StartsWith("no such table: ").Because(sql);
        }
    }

    [Test]
    public async Task Schema_listings_show_only_customer_objects()
    {
        await using var db = await StartAsync();
        var body = Faker.Lorem.Sentence();
        await db.OkAsync(
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)",
            "CREATE INDEX notes_body ON notes (body)",
            "CREATE VIEW note_names AS SELECT name FROM sqlite_schema");
        await db.OkAsync(Stmt("INSERT INTO notes (body) VALUES (?)", body));

        var all = await db.OkAsync(
            "SELECT type, name, tbl_name FROM sqlite_schema ORDER BY name",
            "SELECT m.name FROM sqlite_master m WHERE m.type = 'table'",
            "SELECT count(*) FROM main.sqlite_schema AS s WHERE s.name LIKE '%tb%' OR s.name LIKE 'turso%' OR s.name LIKE '%collections'",
            "SELECT sqlite_schema.name FROM sqlite_schema WHERE sqlite_schema.type = 'index'",
            "SELECT n.body, (SELECT count(*) FROM sqlite_schema) FROM notes n",
            "SELECT name FROM note_names ORDER BY name",
            "PRAGMA table_list",
            "PRAGMA foreign_key_check",
            "EXPLAIN SELECT * FROM sqlite_schema");

        await Assert.That(Rows(all[0])).IsEquivalentTo(["table,notes,notes", "index,notes_body,notes", "view,note_names,note_names"]);
        await Assert.That(Rows(all[1])).IsEquivalentTo(["notes"]);
        await Assert.That(Rows(all[2])).IsEquivalentTo(["0"]);
        await Assert.That(Rows(all[3])).IsEquivalentTo(["notes_body"]);
        await Assert.That(Rows(all[4])).IsEquivalentTo([$"{body},3"]);
        await Assert.That(Rows(all[5])).IsEquivalentTo(["note_names", "notes", "notes_body"]);
        await Assert.That(all[6].GetProperty("rows").EnumerateArray().Select(row => row[1].GetString()!).Where(name => !name.StartsWith("sqlite_", StringComparison.Ordinal)))
            .IsEquivalentTo(["notes", "note_names"]);
        await Assert.That(all[7].GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await Assert.That(all[8].GetProperty("rows").GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task Psql_refuses_escapes_and_hides_internal_tables()
    {
        await using var db = await StartAsync();
        await using var psql = await PostgresWireClient.ConnectAsync(db.Api.Server.PostgresPort, db.WriterKey, db.Database.Name);
        await psql.QueryAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)");

        foreach (var sql in Denied)
        {
            await Assert.That(ErrorState(await psql.QueryAsync(sql))).IsEqualTo("42501").Because(sql);
        }

        foreach (var sql in Internal)
        {
            await Assert.That(ErrorState(await psql.QueryAsync(sql))).IsEqualTo("42P01").Because(sql);
        }

        var listed = await psql.QueryAsync("SELECT type, name FROM sqlite_schema ORDER BY name; SELECT count(*) FROM sqlite_master WHERE substr(name, 1, 1) = '_'");
        await Assert.That(listed.Where(message => message.Type == 'D').Select(message => string.Join(',', message.Fields))).IsEquivalentTo(["table,notes", "0"]);
        await Assert.That(listed.Any(message => message.Type == 'E')).IsFalse();
    }

    private const string SqlSandboxCode = "sql_sandbox";

    private static string? ErrorState(List<(char Type, string?[] Fields)> messages) =>
        messages.Where(message => message.Type == 'E').Select(message => message.Fields[0]).FirstOrDefault();

    private static string[] Rows(JsonElement result) =>
        [.. result.GetProperty("rows").EnumerateArray().Select(row => string.Join(',', row.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString())))];
}
