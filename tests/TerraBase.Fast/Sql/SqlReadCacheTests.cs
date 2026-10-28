using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

// A repeated SELECT reuses its parse and compilation (read-cache.md): every answer is still the statement's over the
// current definitions, data and arguments.
public sealed class SqlReadCacheTests
{
    private static async Task<string> RowsAsync(DocumentApi api, object statement, string? database = null)
    {
        var response = await DocumentApi.SendAsync(api.Client, HttpMethod.Post, $"{api.DatabasePath(database ?? DocumentApi.Database)}/sql/execute",
            JsonContent.Create(new { statements = new[] { statement } }));
        response.Status.ShouldBe(HttpStatusCode.OK);
        var result = response.Json.GetProperty("results")[0];
        var columns = result.GetProperty("columns").EnumerateArray()
            .Select(column => $"{column.GetProperty("name").GetString()}:{(column.TryGetProperty("decltype", out var type) ? type.GetString() : null)}");
        return $"{string.Join(',', columns)} {result.GetProperty("rows").GetRawText()}";
    }

    [Test]
    public async Task A_repeated_read_answers_with_its_arguments_and_the_current_rows()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE kv (k TEXT PRIMARY KEY, name TEXT, n INTEGER)",
            "INSERT INTO kv VALUES ('a', 'ann', 1), ('b', 'bob', 2)");

        var first = await RowsAsync(api, new { sql = "SELECT name FROM kv WHERE k = ?", args = new object[] { "a" } });
        var second = await RowsAsync(api, new { sql = "SELECT name FROM kv WHERE k = ?", args = new object[] { "b" } });
        var literal = await RowsAsync(api, new { sql = "SELECT name FROM kv WHERE k = 'b'" });
        var range = await RowsAsync(api, new { sql = "SELECT name FROM kv WHERE n >= ? ORDER BY n", args = new object[] { 1 } });
        await SqlApi.OkAsync(api, "UPDATE kv SET name = 'zed' WHERE k = 'b'", "INSERT INTO kv VALUES ('c', 'cy', 3)");
        var changed = await RowsAsync(api, new { sql = "SELECT name FROM kv WHERE k = 'b'" });
        var widened = await RowsAsync(api, new { sql = "SELECT name FROM kv WHERE n >= ? ORDER BY n", args = new object[] { 2 } });

        await Assert.That(first).IsEqualTo("""name:TEXT [["ann"]]""");
        await Assert.That(second).IsEqualTo("""name:TEXT [["bob"]]""");
        await Assert.That(literal).IsEqualTo("""name:TEXT [["bob"]]""");
        await Assert.That(range).IsEqualTo("""name:TEXT [["ann"],["bob"]]""");
        await Assert.That(changed).IsEqualTo("""name:TEXT [["zed"]]""");
        await Assert.That(widened).IsEqualTo("""name:TEXT [["zed"],["cy"]]""");
    }

    [Test]
    public async Task A_repeated_read_follows_a_recreated_table_the_catalog_and_its_database()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (k TEXT PRIMARY KEY, a TEXT)", "INSERT INTO t VALUES ('1', 'x')");
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"))).Status.ShouldBe(HttpStatusCode.Created);
        (await DocumentApi.SendAsync(api.Client, HttpMethod.Post, $"{api.DatabasePath("other")}/sql/execute", JsonContent.Create(new
        {
            statements = new object[] { new { sql = "CREATE TABLE t (k INTEGER PRIMARY KEY, other TEXT)" }, new { sql = "INSERT INTO t VALUES (7, 'o')" } },
        }))).Status.ShouldBe(HttpStatusCode.OK);

        var before = await RowsAsync(api, new { sql = "SELECT * FROM t" });
        var tables = await RowsAsync(api, new { sql = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name" });
        var elsewhere = await RowsAsync(api, new { sql = "SELECT * FROM t" }, "other");
        await SqlApi.OkAsync(api, "DROP TABLE t", "CREATE TABLE t (k TEXT PRIMARY KEY, b INTEGER, c TEXT)", "CREATE TABLE u (id INTEGER PRIMARY KEY)",
            "INSERT INTO t VALUES ('2', 5, 'y')");
        var after = await RowsAsync(api, new { sql = "SELECT * FROM t" });
        var more = await RowsAsync(api, new { sql = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name" });

        await Assert.That(before).IsEqualTo("""k:TEXT,a:TEXT [["1","x"]]""");
        await Assert.That(tables).IsEqualTo("""name: [["t"]]""");
        await Assert.That(elsewhere).IsEqualTo("""k:INTEGER,other:TEXT [[7,"o"]]""");
        await Assert.That(after).IsEqualTo("""k:TEXT,b:INTEGER,c:TEXT [["2",5,"y"]]""");
        await Assert.That(more).IsEqualTo("""name: [["t"],["u"]]""");
    }
}
