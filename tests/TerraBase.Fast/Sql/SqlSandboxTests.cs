using System.Net;
using System.Text.Json;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlSandboxTests
{
    private static readonly string[] Denied =
    [
        "ATTACH DATABASE 'other.db' AS other",
        "DETACH DATABASE main",
        "VACUUM INTO '/tmp/terrabase-escape.db'",
        "EXPLAIN ATTACH DATABASE 'other.db' AS other",
        "SELECT load_extension('/tmp/evil.so')",
        "SELECT readfile('/etc/passwd')",
        "SELECT * FROM sqlite_dbpage",
        "CREATE VIRTUAL TABLE leak USING csv(filename = '/etc/passwd')",
        "CREATE TEMP TABLE scratch (id INTEGER)",
        "DELETE FROM sqlite_schema",
        "PRAGMA writable_schema = 1",
        "PRAGMA journal_mode = DELETE",
        "PRAGMA database_list",
        "CREATE TABLE _ts_meta (id INTEGER PRIMARY KEY)",
    ];

    private static readonly string[] Internal =
    [
        "SELECT * FROM turso_cdc",
        "SELECT count(*) FROM turso_sync_cdc",
        "SELECT * FROM \"_TB_STATE\"",
        "SELECT * FROM _ts_meta",
        "SELECT * FROM _collections",
        "PRAGMA table_info(turso_cdc)",
    ];

    [Test]
    public async Task Escape_statements_are_refused_with_sql_sandbox_over_rest_and_psql()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)");
        await using var psql = await PostgresWireClient.ConnectAsync(db.Api.Server.PostgresPort, db.WriterKey, db.Database.Name);

        foreach (var sql in Denied)
        {
            var (status, body) = await db.PostAsync(db.Writer, Stmt(sql));
            var error = body.GetProperty("error");
            await Assert.That((status, error.GetProperty("code").GetString(), error.GetProperty("sqlstate").GetString()))
                .IsEqualTo((HttpStatusCode.Forbidden, "sql_sandbox", "42501")).Because(sql);
            await Assert.That(ErrorState(await psql.QueryAsync(sql))).IsEqualTo("42501").Because(sql);
        }

        await Assert.That(await db.CountAsync("notes")).IsEqualTo(0);
    }

    [Test]
    public async Task Internal_tables_are_invisible_to_queries_and_schema_listings()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)", "CREATE INDEX notes_body ON notes (body)");
        await using var psql = await PostgresWireClient.ConnectAsync(db.Api.Server.PostgresPort, db.WriterKey, db.Database.Name);

        foreach (var sql in Internal)
        {
            var (status, body) = await db.PostAsync(db.Writer, Stmt(sql));
            var error = body.GetProperty("error");
            await Assert.That((status, error.GetProperty("code").GetString())).IsEqualTo((HttpStatusCode.BadRequest, "undefined_table")).Because(sql);
            await Assert.That(error.GetProperty("message").GetString()).StartsWith("no such table: ").Because(sql);
            await Assert.That(ErrorState(await psql.QueryAsync(sql))).IsEqualTo("42P01").Because(sql);
        }

        var listings = await db.OkAsync(
            "SELECT type, name FROM sqlite_schema ORDER BY name",
            "SELECT m.name FROM sqlite_master m WHERE m.type = 'table'",
            "PRAGMA table_list",
            "SELECT count(*) FROM sqlite_sequence");
        await Assert.That(listings[3].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(0);
        var wire = await psql.QueryAsync("SELECT type, name FROM sqlite_schema ORDER BY name");

        await Assert.That(Rows(listings[0])).IsEquivalentTo(["table,notes", "index,notes_body"]);
        await Assert.That(Rows(listings[1])).IsEquivalentTo(["notes"]);
        await Assert.That(listings[2].GetProperty("rows").EnumerateArray().Select(row => row[1].GetString()!).Where(name => !name.StartsWith("sqlite_", StringComparison.Ordinal)))
            .IsEquivalentTo(["notes"]);
        await Assert.That(wire.Where(message => message.Type == 'D').Select(message => string.Join(',', message.Fields))).IsEquivalentTo(["index,notes_body", "table,notes"]);
    }

    private static string? ErrorState(List<(char Type, string?[] Fields)> messages) =>
        messages.Where(message => message.Type == 'E').Select(message => message.Fields[0]).FirstOrDefault();

    private static string[] Rows(JsonElement result) =>
        [.. result.GetProperty("rows").EnumerateArray().Select(row => string.Join(',', row.EnumerateArray().Select(value => value.GetString())))];
}
