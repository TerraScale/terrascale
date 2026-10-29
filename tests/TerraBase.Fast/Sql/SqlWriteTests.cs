using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlWriteTests
{
    private static async Task<JsonElement> OneAsync(DocumentApi api, object statement)
    {
        var response = await SqlApi.SendAsync(api, api.Client, statement);
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? "SQL response");
        return response.Json.GetProperty("results")[0];
    }

    private static async Task<string> RowsAsync(DocumentApi api, string sql) =>
        (await OneAsync(api, new { sql })).GetProperty("rows").GetRawText();

    private static async Task<string?> RefusedAsync(DocumentApi api, object statement) =>
        SqlApi.Error(await SqlApi.SendAsync(api, api.Client, statement)).Sqlite;

    [Test]
    public async Task Sql_tables_take_any_key_while_the_documents_api_takes_only_ulids()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE codes (code TEXT PRIMARY KEY, v INTEGER)",
            "CREATE TABLE nums (id INTEGER PRIMARY KEY, v INTEGER)",
            "CREATE TABLE people (id TEXT PRIMARY KEY, age INTEGER)",
            "INSERT INTO codes VALUES ('abc', 1)",
            "INSERT INTO nums VALUES (42, 2)",
            "INSERT INTO people (id, age) VALUES ('p-1', 20)");

        await Assert.That(await RowsAsync(api, "SELECT code, v FROM codes WHERE code = 'abc'")).IsEqualTo("""[["abc",1]]""");
        await Assert.That(await RowsAsync(api, "SELECT id, v FROM nums WHERE id = 42")).IsEqualTo("""[[42,2]]""");
        // A document write drops a root id equal to its key; a TEXT key named id still reads back.
        await Assert.That(await RowsAsync(api, "SELECT id, age FROM people WHERE id = 'p-1'")).IsEqualTo("""[["p-1",20]]""");

        using var rejected = await api.Client.GetAsync($"{api.Collections}/codes/documents/by-key?id=abc");
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Assert.That((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_document_id");
    }

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
        // Keys are document ids: a single key's canonical value, a composite key's JSON array; not ULIDs, so
        // the documents API refuses them.
        var documents = api.Server.Services.GetRequiredService<IDocumentService>();
        var tenant = await api.TenantOfAsync();
        foreach (var (table, id) in new[] { ("ints", "2"), ("codes", "7"), ("pairs", "[1,\"x\"]") })
        {
            await Assert.That(await documents.GetAsync(new DocumentKey(tenant, table, id), CancellationToken.None) is StoredDocument { Deleted: false })
                .IsTrue().Because($"{table}/{id}");
        }

        var byKey = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/ints/documents/by-key?id=2");
        await Assert.That(byKey.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(byKey.Code).IsEqualTo("invalid_document_id");

        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES (1, 'dup')" })).IsEqualTo("SQLITE_CONSTRAINT_PRIMARYKEY");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id) VALUES (9)" })).IsEqualTo("SQLITE_CONSTRAINT_NOTNULL");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES ('x', 'y')" })).IsEqualTo("SQLITE_MISMATCH");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO u (id, email) VALUES (2, 'a@x')" })).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
        await Assert.That(await RefusedAsync(api, new { sql = "INSERT INTO ints (id, name) VALUES (5, 'x') RETURNING length(name)" })).IsEqualTo("SQLITE_ERROR");
        await Assert.That(await RowsAsync(api, "SELECT count(*) FROM ints")).IsEqualTo("[[3]]");
    }

    [Test]
    public async Task Updates_set_unset_and_increment_and_deletes_remove_rows_with_or_without_keys()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE ints (id INTEGER PRIMARY KEY, name TEXT NOT NULL, n INTEGER DEFAULT 0, r REAL)",
            "CREATE TABLE logs (msg TEXT)",
            "INSERT INTO ints (id, name) VALUES (1, 'ann'), (2, 'bob')",
            "INSERT INTO logs (msg) SELECT name FROM ints");

        var updated = await OneAsync(api, new { sql = "UPDATE ints SET n = n + 5, r = ? * 2 WHERE id = ?", args = new object[] { 0.75, 1 } });
        await OneAsync(api, new { sql = "UPDATE ints SET n = n - 1, r = NULL WHERE name = 'bob'" });
        var unkeyed = await OneAsync(api, new { sql = "UPDATE logs SET msg = upper(msg)" });

        await Assert.That(updated.GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(await RowsAsync(api, "SELECT id, n, r FROM ints ORDER BY id")).IsEqualTo("""[[1,5,1.5],[2,-1,null]]""");
        await Assert.That(unkeyed.GetProperty("rows_affected").GetInt64()).IsEqualTo(2);
        await Assert.That(await RowsAsync(api, "SELECT msg FROM logs ORDER BY msg")).IsEqualTo("""[["ANN"],["BOB"]]""");
        await Assert.That(await RefusedAsync(api, new { sql = "UPDATE ints SET name = NULL WHERE id = 1" })).IsEqualTo("SQLITE_CONSTRAINT_NOTNULL");
        await Assert.That(await RefusedAsync(api, new { sql = "UPDATE ints SET id = 9 WHERE id = 1" })).IsEqualTo("SQLITE_ERROR");

        var deleted = await OneAsync(api, new { sql = "DELETE FROM ints WHERE id = 2" });
        var cleared = await OneAsync(api, new { sql = "DELETE FROM logs" });

        await Assert.That(deleted.GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(cleared.GetProperty("rows_affected").GetInt64()).IsEqualTo(2);
        await Assert.That(await RowsAsync(api, "SELECT (SELECT count(*) FROM ints), (SELECT count(*) FROM logs)")).IsEqualTo("[[1,0]]");
    }

    [Test]
    public async Task Keys_keep_their_type_outside_integer_and_text_affinity_and_generated_keys_stay_fresh()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE loose (k PRIMARY KEY, v TEXT)", "CREATE TABLE gen (id INTEGER PRIMARY KEY, v TEXT)");
        await using var session = SqlHarness.Open(api, canWrite: true);

        await OneAsync(api, new { sql = "INSERT INTO loose VALUES (1, 'integer')" });
        var text = await OneAsync(api, new { sql = "INSERT INTO loose VALUES ('1', 'text')" });
        await OneAsync(api, new { sql = "REPLACE INTO loose VALUES ('1', 'text again')" });
        // Generated keys of autocommit inserts are not logged, so a later transaction never reuses them.
        for (var i = 0; i < 10; i++)
        {
            await Assert.That(SqlHarness.Code(await session.ExecuteAsync(SqlHarness.Command("INSERT INTO gen (v) VALUES ('auto')"), CancellationToken.None))).IsNull();
        }

        await session.ExecuteAsync(SqlHarness.Command("BEGIN"), CancellationToken.None);
        var inTransaction = await session.ExecuteAsync(SqlHarness.Command("INSERT INTO gen (v) VALUES ('tx')"), CancellationToken.None);
        var committed = await session.ExecuteAsync(SqlHarness.Command("COMMIT"), CancellationToken.None);

        await Assert.That(text.GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(await RowsAsync(api, "SELECT k, typeof(k), v FROM loose ORDER BY v"))
            .IsEqualTo("""[[1,"integer","integer"],["1","text","text again"]]""");
        await Assert.That(SqlHarness.Code(inTransaction)).IsNull();
        await Assert.That(SqlHarness.Code(committed)).IsNull();
        await Assert.That(await RowsAsync(api, "SELECT count(*) FROM gen")).IsEqualTo("[[11]]");
    }

    [Test]
    public async Task Increments_are_counters_only_on_integer_not_null_columns_and_requests_hold_256_statements()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE c (id INTEGER PRIMARY KEY, counter INTEGER NOT NULL, loose INTEGER, r REAL)",
            "INSERT INTO c (id, counter, r) VALUES (1, 1, 0.5)");

        await OneAsync(api, new { sql = "UPDATE c SET counter = counter + 2, loose = loose + 1, r = r + 1 WHERE id = 1" });
        var tooMany = await SqlApi.ExecuteAsync(api, [.. Enumerable.Repeat("SELECT 1", 257)]);

        // NULL + 1 stays NULL, as in SQLite; a REAL column takes the computed value.
        await Assert.That(await RowsAsync(api, "SELECT counter, loose, r FROM c")).IsEqualTo("[[3,null,1.5]]");
        await Assert.That((tooMany.Status, tooMany.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
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

    [Test]
    public async Task Literals_with_doubled_quotes_round_trip()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE q (id INTEGER PRIMARY KEY, v TEXT)");

        var results = await SqlApi.OkAsync(api,
            "INSERT INTO q VALUES (1, 'it''''s'), (2, ''''''), (3, 'a''b''''c')",
            "SELECT v FROM q ORDER BY id",
            "SELECT '''it''''s''' AS v",
            "SELECT count(*) FROM q WHERE v = 'it''''s'");

        await Assert.That(string.Join("|", results[1].GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()))).IsEqualTo("it''s|''|a'b''c");
        await Assert.That(results[2].GetProperty("rows")[0][0].GetString()).IsEqualTo("'it''s'");
        await Assert.That(results[3].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
    }
}
