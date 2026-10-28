using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

// A read of one table by its single TEXT primary key reads that row through the id index, not every row of the table
// (relational-sql.md section 4, key-point-reads.md).
public sealed class SqlKeyReadTests
{
    private static async Task<string> RowsAsync(DocumentApi api, object statement)
    {
        var response = await SqlApi.SendAsync(api, api.Client, statement);
        response.Status.ShouldBe(HttpStatusCode.OK);
        return response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText();
    }

    [Test]
    public async Task Reads_by_a_text_key_answer_as_the_statement_says()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE kv (k TEXT PRIMARY KEY, name TEXT, n INTEGER)",
            "INSERT INTO kv VALUES ('a', 'ann', 1), ('b', 'bob', 2), ('7', 'sev', 7)");

        (object Statement, string Rows)[] cases =
        [
            (new { sql = "SELECT name FROM kv WHERE k = ?", args = new object[] { "b" } }, """[["bob"]]"""),
            (new { sql = "SELECT name FROM kv WHERE ? = k AND n > 1", args = new object[] { "b" } }, """[["bob"]]"""),
            (new { sql = "SELECT name FROM kv WHERE (n > 5 AND K = ?)", args = new object[] { "b" } }, "[]"),
            (new { sql = "SELECT v.name FROM kv AS v WHERE v.k = 'a'" }, """[["ann"]]"""),
            (new { sql = "SELECT name FROM kv WHERE k = :key", named = new { key = "7" } }, """[["sev"]]"""),
            (new { sql = "SELECT name FROM kv WHERE k = ?", args = new object[] { 7 } }, """[["sev"]]"""),
            (new { sql = "SELECT count(*) FROM kv WHERE k = ?", args = new object[] { "zz" } }, "[[0]]"),
            (new { sql = "SELECT name FROM kv WHERE k = ? OR k = 'a' ORDER BY name", args = new object[] { "b" } }, """[["ann"],["bob"]]"""),
            (new { sql = "SELECT name FROM kv WHERE k = ? AND k IN (SELECT k FROM kv WHERE n = 1)", args = new object[] { "a" } }, """[["ann"]]"""),
            (new { sql = "SELECT x.name, y.name FROM kv x JOIN kv y ON y.k = 'a' WHERE x.k = ?", args = new object[] { "b" } }, """[["bob","ann"]]"""),
        ];

        foreach (var (statement, rows) in cases)
        {
            await Assert.That(await RowsAsync(api, statement)).IsEqualTo(rows);
        }
    }

    // A Hobby read scans at most 10,000 rows: a scan of this table is refused, a read or write of one row by key is not.
    [Test]
    public async Task Reads_and_writes_by_key_scan_only_their_row()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE big (k TEXT PRIMARY KEY, name TEXT)",
            "INSERT INTO big VALUES ('a', 'ann'), ('b', 'bob')");
        // More rows than a Hobby read may scan, stored straight in the collection's table.
        await using (var connection = new Turso.Data.Sqlite.SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync())))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 12000)
                INSERT INTO "big" (id, doc) SELECT 'r' || i, json_object('name', 'n' || i) FROM n;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var scan = await SqlApi.ExecuteAsync(api, "SELECT count(*) FROM big");
        var read = await RowsAsync(api, new { sql = "SELECT name FROM big WHERE k = ?", args = new object[] { "r11999" } });
        var update = await SqlApi.SendAsync(api, api.Client, new { sql = "UPDATE big SET name = 'z' WHERE k = ?", args = new object[] { "a" } });
        var delete = await SqlApi.SendAsync(api, api.Client, new { sql = "DELETE FROM big WHERE k = 'b'" });

        await Assert.That(SqlApi.Error(scan)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_INTERRUPT", 0));
        await Assert.That(read).IsEqualTo("""[["n11999"]]""");
        await Assert.That((update.Status, delete.Status)).IsEqualTo((HttpStatusCode.OK, HttpStatusCode.OK));
        await Assert.That(await RowsAsync(api, new { sql = "SELECT k, name FROM big WHERE k = 'a'" })).IsEqualTo("""[["a","z"]]""");
        await Assert.That(await RowsAsync(api, new { sql = "SELECT name FROM big WHERE k = ?", args = new object[] { "b" } })).IsEqualTo("[]");
        await Assert.That(SqlApi.Error(await SqlApi.SendAsync(api, api.Client, new { sql = "SELECT k FROM big WHERE k = ? OR k = 'b'", args = new object[] { "a" } })).Sqlite)
            .IsEqualTo("SQLITE_INTERRUPT");
    }
}
