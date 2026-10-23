using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlWriteTests
{
    private static async Task<JsonElement> OneAsync(DocumentApi api, object statement)
    {
        var response = await SqlApi.SendAsync(api, api.Client, statement);
        response.Status.ShouldBe(HttpStatusCode.OK);
        return response.Json.GetProperty("results")[0];
    }

    private static async Task<string> RowsAsync(DocumentApi api, string sql) =>
        (await OneAsync(api, new { sql })).GetProperty("rows").GetRawText();

    private static async Task<string?> RefusedAsync(DocumentApi api, object statement) =>
        SqlApi.Error(await SqlApi.SendAsync(api, api.Client, statement)).Sqlite;

    [Test]
    public async Task Inserts_take_keys_affinity_defaults_and_conflict_modes()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE ints (id INTEGER PRIMARY KEY, name TEXT NOT NULL, n INTEGER DEFAULT 7, r REAL)",
            "CREATE TABLE codes (code TEXT PRIMARY KEY, v)",
            "CREATE TABLE pairs (a INTEGER, b TEXT, PRIMARY KEY (a, b))",
            "CREATE TABLE u (id INTEGER PRIMARY KEY, email TEXT UNIQUE)");

        var first = await OneAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES (1, 'ann')" });
        // A JavaScript client sends 2 as 2.0: INTEGER affinity stores the integer, and the key is "2".
        var real = await OneAsync(api, new { sql = "INSERT INTO ints (id, name, r) VALUES (?, ?, ?)", args = new object[] { 2.0, "bob", 3 } });
        var generated = await OneAsync(api, new { sql = "INSERT INTO ints (name) VALUES ('gen')" });
        var ignored = await OneAsync(api, new { sql = "INSERT OR IGNORE INTO ints (id, name) VALUES (1, 'dup')" });
        var nothing = await OneAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES (1, 'dup') ON CONFLICT DO NOTHING" });
        await OneAsync(api, new { sql = "INSERT OR REPLACE INTO ints (id, name) VALUES (1, 'ann2')" });
        await OneAsync(api, new { sql = "INSERT INTO codes VALUES (7, 'x')" });
        await OneAsync(api, new { sql = "INSERT INTO pairs VALUES (1, 'x')" });
        await OneAsync(api, new { sql = "INSERT INTO u (id, email) VALUES (1, 'a@x')" });

        await Assert.That(first.GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(first.GetProperty("last_insert_rowid").GetInt64()).IsEqualTo(1);
        await Assert.That(real.GetProperty("last_insert_rowid").GetInt64()).IsEqualTo(2);
        var key = generated.GetProperty("last_insert_rowid").GetInt64();
        await Assert.That(key).IsGreaterThan(0).And.IsLessThan(1L << 53);
        await Assert.That(ignored.GetProperty("rows_affected").GetInt64()).IsEqualTo(0);
        await Assert.That(nothing.GetProperty("rows_affected").GetInt64()).IsEqualTo(0);
        await Assert.That(await RowsAsync(api, "SELECT id, typeof(id), name, n, r FROM ints WHERE id <= 2 ORDER BY id"))
            .IsEqualTo("""[[1,"integer","ann2",7,null],[2,"integer","bob",7,3]]""");
        await Assert.That(await RowsAsync(api, $"SELECT name FROM ints WHERE id = {key}")).IsEqualTo("""[["gen"]]""");
        await Assert.That(await RowsAsync(api, "SELECT code, typeof(code) FROM codes")).IsEqualTo("""[["7","text"]]""");
        // Keys are document ids: a single key's canonical value, a composite key's JSON array.
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/ints/documents/by-key?id=2")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/codes/documents/by-key?id=7")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/pairs/documents/by-key?id={Uri.EscapeDataString("[1,\"x\"]")}")).Status)
            .IsEqualTo(HttpStatusCode.OK);

        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES (1, 'dup')" })).IsEqualTo("SQLITE_CONSTRAINT_PRIMARYKEY");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id) VALUES (9)" })).IsEqualTo("SQLITE_CONSTRAINT_NOTNULL");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES ('x', 'y')" })).IsEqualTo("SQLITE_MISMATCH");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO u (id, email) VALUES (2, 'a@x')" })).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES (5, 'x') RETURNING id" })).IsEqualTo("SQLITE_ERROR");
        await Assert.That(await RowsAsync(api, "SELECT count(*) FROM ints")).IsEqualTo("[[3]]");
    }

    [Test]
    public async Task A_statement_writing_more_than_1024_rows_is_too_big_and_writes_nothing()
    {
        await using var api = await DocumentApi.StartAsync();
        const string digits = "(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))";
        await SqlApi.OkAsync(api, "CREATE TABLE logs (msg TEXT)");

        var tooMany = await SqlApi.ExecuteAsync(api, $"INSERT INTO logs (msg) SELECT 'x' FROM {digits} a, {digits} b, {digits} c, (VALUES (0),(1)) d");

        await Assert.That(SqlApi.Error(tooMany)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "sql_too_large", "SQLITE_TOOBIG", 0));
        await Assert.That(await RowsAsync(api, "SELECT count(*) FROM logs")).IsEqualTo("[[0]]");
    }
}
