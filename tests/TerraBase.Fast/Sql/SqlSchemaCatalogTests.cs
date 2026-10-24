using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

/// <summary>sqlite_master and the catalog pragmas, as drizzle-kit and SQLAlchemy read them.</summary>
public sealed class SqlSchemaCatalogTests
{
    private static readonly string[] Schema =
    [
        "CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email TEXT NOT NULL UNIQUE, name TEXT DEFAULT 'it''s')",
        "CREATE TABLE `posts` (`id` integer PRIMARY KEY NOT NULL, `user_id` integer REFERENCES `users`(`id`) ON DELETE CASCADE, `slug` text, `tag` text, FOREIGN KEY (`tag`) REFERENCES tags(name))",
        "CREATE TABLE tags (name TEXT PRIMARY KEY)",
        "CREATE INDEX posts_user ON posts (user_id)",
        "CREATE UNIQUE INDEX `posts_slug_unique` ON `posts` (`slug`)",
    ];

    // drizzle-kit 0.31's SQLite introspection, as it sends it (filter of ignored tables included).
    private const string DrizzleColumns = """
        SELECT m.name as "tableName", p.name as "columnName", p.type as "columnType", p."notnull" as "notNull", p.dflt_value as "defaultValue", p.pk as pk, p.hidden as hidden, m.sql, m.type as type FROM sqlite_master AS m JOIN pragma_table_xinfo(m.name) AS p WHERE (m.type = 'table' OR m.type = 'view') AND m.tbl_name != 'sqlite_sequence' AND m.tbl_name != 'sqlite_stat1' AND m.tbl_name != '_litestream_seq' AND m.tbl_name != '_litestream_lock' AND m.tbl_name != 'libsql_wasm_func_table' AND m.tbl_name != '__drizzle_migrations' AND m.tbl_name != '_cf_KV';
        """;

    private const string DrizzleForeignKeys = """
        SELECT m.name as "tableFrom", f.id as "id", f."table" as "tableTo", f."from", f."to", f."on_update" as "onUpdate", f."on_delete" as "onDelete", f.seq as "seq" FROM sqlite_master m, pragma_foreign_key_list(m.name) as f WHERE m.tbl_name != 'sqlite_sequence';
        """;

    private const string DrizzleIndexes = """
        SELECT m.tbl_name as tableName, il.name as indexName, ii.name as columnName, il.[unique] as isUnique, il.seq as seq FROM sqlite_master AS m, pragma_index_list(m.name) AS il, pragma_index_info(il.name) AS ii WHERE m.type = 'table' AND il.name NOT LIKE 'sqlite\_autoindex\_%' ESCAPE '\' AND m.tbl_name != 'sqlite_sequence';
        """;

    [Test]
    public async Task Drizzle_kit_introspection_reads_columns_foreign_keys_and_indexes()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Schema);

        var results = await SqlApi.OkAsync(api, DrizzleColumns, DrizzleForeignKeys, DrizzleIndexes, "SELECT name AS view_name, sql AS sql FROM sqlite_master WHERE type = 'view';");

        // tableName|columnName|columnType|notNull|defaultValue|pk|hidden (sql and type checked apart).
        var columns = Rows(results[0]);
        await Assert.That(string.Join(";", columns.Select(row => string.Join("|", row.Take(7))))).IsEqualTo(
            "posts|id|INTEGER|1||1|0;posts|user_id|INTEGER|0||0|0;posts|slug|TEXT|0||0|0;posts|tag|TEXT|0||0|0;tags|name|TEXT|1||1|0;"
            + "users|id|INTEGER|1||1|0;users|email|TEXT|1||0|0;users|name|TEXT|0|'it''s'|0|0");
        await Assert.That(columns.All(row => row[8] == "table")).IsTrue();
        await Assert.That(columns[0][7]!).Contains("REFERENCES");
        await Assert.That(columns[5][7]!).Contains("AUTOINCREMENT");
        await Assert.That(Flat(results[1])).IsEqualTo("posts|0|users|user_id|id|NO ACTION|CASCADE|0;posts|1|tags|tag|name|NO ACTION|NO ACTION|0");
        await Assert.That(Flat(results[2])).IsEqualTo("posts|posts_slug_unique|slug|1|0;posts|posts_user|user_id|0|1");
        await Assert.That(Flat(results[3])).IsEqualTo("");
    }

    [Test]
    public async Task Sqlalchemy_reflection_reads_sqlite_master_and_the_catalog_pragmas()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Schema);

        var results = await SqlApi.OkAsync(api,
            "SELECT name FROM \"main\".sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite~_%' ESCAPE '~' ORDER BY name",
            "PRAGMA main.table_xinfo(\"users\")",
            "PRAGMA main.index_list(\"users\")",
            "PRAGMA main.index_info(\"sqlite_autoindex_users_1\")",
            "PRAGMA main.foreign_key_list(\"posts\")",
            "PRAGMA main.index_list(\"posts\")",
            "SELECT sql FROM \"main\".sqlite_master WHERE name = 'posts_user' AND type = 'index'",
            "PRAGMA main.table_xinfo(\"nothing\")",
            "SELECT count(*) FROM sqlite_temp_master");

        await Assert.That(Flat(results[0])).IsEqualTo("posts;tags;users");
        await Assert.That(Names(results[1])).IsEqualTo("cid|name|type|notnull|dflt_value|pk|hidden");
        await Assert.That(Flat(results[1])).IsEqualTo("0|id|INTEGER|1||1|0;1|email|TEXT|1||0|0;2|name|TEXT|0|'it''s'|0|0");
        await Assert.That(Flat(results[2])).IsEqualTo("0|sqlite_autoindex_users_1|1|u|0");
        await Assert.That(Flat(results[3])).IsEqualTo("0|1|email");
        await Assert.That(Names(results[4])).IsEqualTo("id|seq|table|from|to|on_update|on_delete|match");
        await Assert.That(Flat(results[4])).IsEqualTo("0|0|users|user_id|id|NO ACTION|CASCADE|NONE;1|0|tags|tag|name|NO ACTION|NO ACTION|NONE");
        await Assert.That(Flat(results[5])).IsEqualTo("0|posts_slug_unique|1|c|0;1|posts_user|0|c|0");
        await Assert.That(Flat(results[6])).IsEqualTo("CREATE INDEX posts_user ON posts (user_id)");
        await Assert.That(Flat(results[7])).IsEqualTo("");
        await Assert.That(Flat(results[8])).IsEqualTo("0");
    }

    [Test]
    public async Task The_catalog_is_read_only_and_follows_ddl_in_the_same_request()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (a TEXT)");

        var write = await SqlApi.ExecuteAsync(api, "INSERT INTO sqlite_master (name) VALUES ('x')");
        var outer = await SqlApi.ExecuteAsync(api, "SELECT m.name FROM sqlite_master m LEFT JOIN pragma_table_info(m.name) p");
        var same = await SqlApi.OkAsync(api, "CREATE TABLE u (b TEXT)", "DROP TABLE t", "SELECT group_concat(name) FROM sqlite_schema WHERE type = 'table'");

        await Assert.That(write.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(SqlApi.Error(outer).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(Flat(same[2])).IsEqualTo("u");
    }

    // drizzle-kit 0.31 finds AUTOINCREMENT columns with an infix GLOB over sqlite_master.sql.
    private const string DrizzleAutoincrement = """
        SELECT
        		  *
        		FROM sqlite_master
        		WHERE sql GLOB '*[ *' || CHAR(9) || CHAR(10) || CHAR(13) || ']AUTOINCREMENT[^'']*'
        	  AND tbl_name != '__drizzle_migrations'
        			AND tbl_name NOT LIKE '\_cf\_%' ESCAPE '\'
        			AND tbl_name NOT LIKE '\_litestream\_%' ESCAPE '\'
        			AND tbl_name NOT LIKE 'libsql\_%' ESCAPE '\'
        			AND tbl_name NOT LIKE 'sqlite\_%' ESCAPE '\';
        """;

    [Test]
    public async Task Catalog_names_never_reach_the_database_files_own_schema_at_any_depth()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Schema);

        // SQLAlchemy's _get_table_sql, with its bound name; and catalog relations in nested scopes.
        var table = await SqlApi.SendAsync(api, api.Client, new
        {
            sql = "SELECT sql FROM (SELECT * FROM sqlite_master UNION ALL SELECT * FROM sqlite_temp_master) WHERE name = ? AND type in ('table', 'view')",
            args = new object[] { "users" },
        });
        var results = await SqlApi.OkAsync(api,
            "SELECT count(*) FROM tags WHERE 'users' IN (SELECT name FROM sqlite_master)",
            "SELECT sqlite_master.name FROM sqlite_master WHERE sqlite_master.type = 'table' ORDER BY 1",
            "SELECT group_concat(sql, ' ') FROM (SELECT sql FROM sqlite_schema) AS s",
            "SELECT count(*) FROM (SELECT * FROM pragma_table_info AS p WHERE p.arg = 'users')",
            DrizzleAutoincrement);

        await Assert.That(table.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(table.Json.GetProperty("results")[0].GetProperty("rows")[0][0].GetString()!).StartsWith("CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT");
        await Assert.That(Flat(results[0])).IsEqualTo("0");
        await Assert.That(Flat(results[1])).IsEqualTo("posts;tags;users");
        // The file's own tables keep each row as a doc column; the catalog never shows them.
        await Assert.That(Flat(results[2])).DoesNotContain(" doc ");
        await Assert.That(Flat(results[3])).IsEqualTo("3");
        await Assert.That(Rows(results[4]).Select(row => row[1]).Single()).IsEqualTo("users");
    }

    [Test]
    public async Task From_subqueries_are_validated_like_the_rest_of_the_statement()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE tags (name TEXT PRIMARY KEY)", "INSERT INTO tags VALUES ('x')");

        var results = await SqlApi.OkAsync(api,
            "SELECT * FROM (SELECT * FROM tags)",
            "SELECT * FROM (SELECT * FROM (SELECT name FROM tags) AS a) AS b");
        string[] refused =
        [
            "SELECT * FROM (SELECT * FROM _tb_state)",
            "SELECT * FROM (SELECT * FROM (SELECT * FROM _collections))",
            "SELECT * FROM (SELECT sqlite_version())",
            "SELECT * FROM (SELECT * FROM nothing)",
        ];

        await Assert.That(Names(results[0])).IsEqualTo("name");
        await Assert.That(Flat(results[1])).IsEqualTo("x");
        foreach (var sql in refused)
        {
            await Assert.That(SqlApi.Error(await SqlApi.ExecuteAsync(api, sql)).Sqlite).IsEqualTo("SQLITE_ERROR").Because(sql);
        }
    }

    [Test]
    public async Task Glob_is_an_operator_and_similar_to_is_refused()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE g (v TEXT)", "INSERT INTO g VALUES ('apple'), ('Apricot'), ('banana')");

        var results = await SqlApi.OkAsync(api,
            "SELECT group_concat(v) FROM g WHERE v GLOB 'a*'",
            "SELECT group_concat(v) FROM g WHERE v NOT GLOB 'a*' ORDER BY v",
            "SELECT count(*) FROM g WHERE glob('*an*', v)");
        var similar = await SqlApi.ExecuteAsync(api, "SELECT v FROM g WHERE v SIMILAR TO 'a%'");

        await Assert.That(Flat(results[0])).IsEqualTo("apple");
        await Assert.That(Flat(results[1])).IsEqualTo("Apricot,banana");
        await Assert.That(Flat(results[2])).IsEqualTo("1");
        await Assert.That(SqlApi.Error(similar).Sqlite).IsEqualTo("SQLITE_ERROR");
    }

    private static string Names(JsonElement result) => string.Join("|", result.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("name").GetString()));

    private static string Flat(JsonElement result) => string.Join(";", Rows(result).Select(row => string.Join("|", row)));

    private static List<string?[]> Rows(JsonElement result) =>
        [.. result.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Null ? null : value.ToString()).ToArray())];
}
