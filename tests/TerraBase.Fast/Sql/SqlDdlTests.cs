using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Sql;

namespace TerraBase.Fast.Sql;

public sealed class SqlDdlTests
{
    [Test]
    public async Task Create_table_stores_the_definition_for_integer_text_composite_and_no_keys()
    {
        await using var api = await DocumentApi.StartAsync();
        var outcomes = await SqlHarness.RunAsync(api, canWrite: true,
            "CREATE TABLE ints (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL DEFAULT 'x', score REAL DEFAULT -1.5)",
            "create table \"Codes\" (code varchar(16) primary key, n int unique);",
            "CREATE TABLE pairs (a INTEGER, b TEXT, v, PRIMARY KEY (a, b), UNIQUE (b, v))",
            "CREATE TABLE plain (v TEXT)",
            "CREATE TABLE IF NOT EXISTS ints (other TEXT)");

        var ints = (await SqlHarness.DefinitionAsync(api, "ints"))!.Value;
        var codes = (await SqlHarness.DefinitionAsync(api, "codes"))!.Value;
        var pairs = (await SqlHarness.DefinitionAsync(api, "pairs"))!.Value;
        var plain = (await SqlHarness.DefinitionAsync(api, "plain"))!.Value;
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await using var transaction = (await engine.BeginTransactionAsync(await api.TenantOfAsync(), write: false, CancellationToken.None))!;
        var unique = await transaction.GetUniqueConstraintsAsync("pairs", CancellationToken.None);

        await Assert.That(outcomes.Select(SqlHarness.Code)).IsEquivalentTo(new string?[5]);
        await Assert.That(outcomes[0] is SqlResult { Columns.Count: 0, Rows.Count: 0, RowsAffected: 0 }).IsTrue();
        await Assert.That(ints.GetRawText()).IsEqualTo(
            """{"columns":[{"name":"id","type":"INTEGER","not_null":true},{"name":"name","type":"TEXT","not_null":true,"default":"x"},{"name":"score","type":"REAL","not_null":false,"default":-1.5}],"primary_key":["id"]}""");
        await Assert.That(codes.GetProperty("primary_key")[0].GetString()).IsEqualTo("code");
        await Assert.That(codes.GetProperty("columns")[0].GetProperty("type").GetString()).IsEqualTo("VARCHAR(16)");
        await Assert.That(pairs.GetProperty("primary_key").GetRawText()).IsEqualTo("""["a","b"]""");
        await Assert.That(pairs.GetProperty("columns")[2].GetProperty("type").GetString()).IsEqualTo("");
        await Assert.That(plain.GetProperty("primary_key").GetArrayLength()).IsEqualTo(0);
        await Assert.That(unique.Single().Fields).IsEquivalentTo(["b", "v"]);
        // A table is a collection: the document API sees it.
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/codes")).Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Drop_table_removes_the_collection_and_document_collections_stay_invisible()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlHarness.RunAsync(api, "CREATE TABLE t (id TEXT PRIMARY KEY)");

        var dropped = await SqlHarness.RunAsync(api, "DROP TABLE t");
        var again = await SqlHarness.RunAsync(api, "DROP TABLE t");
        var ifExists = await SqlHarness.RunAsync(api, "DROP TABLE IF EXISTS t");
        var recreated = await SqlHarness.RunAsync(api, "CREATE TABLE t (other INTEGER)");
        // "items" is a document collection: SQL neither drops nor shadows it.
        var dropItems = await SqlHarness.RunAsync(api, "DROP TABLE items");
        var createItems = await SqlHarness.RunAsync(api, "CREATE TABLE IF NOT EXISTS items (a TEXT)");

        await Assert.That(SqlHarness.Code(dropped)).IsNull();
        await Assert.That(SqlHarness.Code(again)).IsEqualTo("SQLITE_ERROR");
        await Assert.That(SqlHarness.Code(ifExists)).IsNull();
        await Assert.That(SqlHarness.Code(recreated)).IsNull();
        await Assert.That((await SqlHarness.DefinitionAsync(api, "t"))!.Value.GetProperty("columns")[0].GetProperty("name").GetString()).IsEqualTo("other");
        await Assert.That(SqlHarness.Code(dropItems)).IsEqualTo("SQLITE_ERROR");
        await Assert.That(SqlHarness.Code(createItems)).IsEqualTo("SQLITE_ERROR");
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/items")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await SqlHarness.DefinitionAsync(api, "items")).IsNull();
    }

    [Test]
    public async Task Unsupported_ddl_is_refused_without_quoting_the_statement()
    {
        await using var api = await DocumentApi.StartAsync();
        string[] refused =
        [
            "CREATE TABLE secret_name (",
            "CREATE TABLE t (a TEXT CHECK (a <> ''))",
            "CREATE TABLE t (a TEXT, FOREIGN KEY (b) REFERENCES u(id))",
            "CREATE TABLE t (a TEXT COLLATE NOCASE)",
            "CREATE TEMP TABLE t (a TEXT)",
            "CREATE TABLE t AS SELECT 1",
            "CREATE TABLE aux.t (a TEXT)",
            "CREATE TABLE sqlite_t (a TEXT)",
            "CREATE TABLE t (a TEXT PRIMARY KEY, b TEXT PRIMARY KEY)",
            "CREATE TABLE t (a TEXT, A INTEGER)",
            "CREATE TABLE t (_a TEXT)",
            "CREATE TABLE t (a TEXT DEFAULT (1 + 2))",
            "CREATE TABLE t (a TEXT); CREATE TABLE u (a TEXT)",
            "PRAGMA table_list",
            "PRAGMA foreign_keys = maybe",
            "DROP VIEW v",
        ];

        foreach (var sql in refused)
        {
            var outcome = await SqlHarness.RunAsync(api, sql);
            await Assert.That(SqlHarness.Code(outcome)).IsEqualTo("SQLITE_ERROR").Because(sql);
            await Assert.That(outcome is SqlError error ? error.Message : "").DoesNotContain("secret_name").Because(sql);
        }

        await Assert.That(await SqlHarness.DefinitionAsync(api, "t")).IsNull();
    }

    [Test]
    public async Task Foreign_keys_are_accepted_but_not_enforced_and_their_pragma_is_a_no_op()
    {
        await using var api = await DocumentApi.StartAsync();
        var pragmas = await SqlHarness.RunAsync(api, canWrite: false, "PRAGMA foreign_keys=off", "pragma main.FOREIGN_KEYS = ON;");
        var outcomes = await SqlHarness.RunAsync(api, canWrite: true,
            "CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE posts (id INTEGER PRIMARY KEY, author_id INTEGER REFERENCES authors(id) ON DELETE CASCADE, editor_id INTEGER, FOREIGN KEY (editor_id) REFERENCES authors (id))",
            // As in SQLite with foreign keys off: a dangling reference is stored.
            "INSERT INTO posts (id, author_id, editor_id) VALUES (1, 42, 43)",
            "SELECT author_id FROM posts");

        await Assert.That(pragmas.Select(SqlHarness.Code)).IsEquivalentTo(new string?[2]);
        await Assert.That(pragmas[0] is SqlResult { Columns.Count: 0, Rows.Count: 0, RowsAffected: 0 }).IsTrue();
        await Assert.That(outcomes.Select(SqlHarness.Code)).IsEquivalentTo(new string?[4]);
        await Assert.That(outcomes[3] is SqlResult { Rows.Count: 1 }).IsTrue();
        await Assert.That((await SqlHarness.DefinitionAsync(api, "posts"))!.Value.GetProperty("columns").GetArrayLength()).IsEqualTo(3);
    }

    [Test]
    public async Task A_serial_primary_key_left_out_of_an_insert_is_generated_like_an_integer_key()
    {
        await using var api = await DocumentApi.StartAsync();
        var outcomes = await SqlHarness.RunAsync(api, canWrite: true,
            "CREATE TABLE IF NOT EXISTS \"drizzle_migrations\" (id SERIAL PRIMARY KEY, hash text NOT NULL, created_at numeric)",
            "INSERT INTO \"drizzle_migrations\" (\"hash\", \"created_at\") VALUES('h1', 1)",
            "INSERT INTO drizzle_migrations (hash, created_at) VALUES('h2', 2)",
            "SELECT count(DISTINCT id), min(typeof(id)) FROM drizzle_migrations");

        await Assert.That(outcomes.Select(SqlHarness.Code)).IsEquivalentTo(new string?[4]);
        await Assert.That(outcomes[1] is SqlResult { LastInsertRowid: > 0 }).IsTrue();
        await Assert.That(outcomes[3] is SqlResult { Rows: [[{ Integer: 2 }, { Text: "integer" }]] }).IsTrue();
    }

    [Test]
    public async Task Ddl_without_write_access_is_sqlite_auth_and_changes_nothing()
    {
        await using var api = await DocumentApi.StartAsync();

        var denied = await SqlHarness.RunAsync(api, canWrite: false, "CREATE TABLE t (a TEXT)");

        await Assert.That(SqlHarness.Code(denied[0])).IsEqualTo("SQLITE_AUTH");
        await Assert.That(await SqlHarness.DefinitionAsync(api, "t")).IsNull();
    }
}
