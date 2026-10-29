using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityScopeReviewTests
{
    [Test]
    [Arguments("SELECT n AS s,t.s FROM t ORDER BY s=:x,n ASC", """[[2,"1"],[1,"01"]]""")]
    [Arguments("SELECT n AS s,t.s FROM t ORDER BY s,n ASC", """[[1,"01"],[2,"1"]]""")]
    [Arguments("SELECT count(*) FROM t a WHERE EXISTS(SELECT 1 FROM other a WHERE a.n=:x)", "[[1]]")]
    [Arguments("SELECT count(*) FROM t a WHERE EXISTS(SELECT 1 FROM other a WHERE a.u=:x)", "[[2]]")]
    [Arguments("SELECT count(*) FROM t,(VALUES(0)) q WHERE n=:x", "[[1]]")]
    [Arguments("SELECT count(*) FROM t,(VALUES(0,1)) q WHERE n=:x AND q.column2=:y", "[[1]]")]
    [Arguments("SELECT count(*) FROM t,(VALUES(0,1)) q WHERE n=:x AND q.column2=:x", "[[0]]")]
    [Arguments("SELECT count(*) FROM t a WHERE EXISTS(SELECT 1 FROM (SELECT a.n AS k) q WHERE k=:x)", "[[1]]")]
    [Arguments("SELECT count(*) FROM t a WHERE EXISTS(SELECT 1 FROM (SELECT a.s AS k) q WHERE k=:x)", "[[1]]")]
    public async Task Name_resolution_preserves_expression_precedence_and_correlated_lineage(string sql, string rows)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER,s TEXT)", "INSERT INTO t VALUES(1,'01'),(2,'1')",
            "CREATE TABLE other(u INTEGER)", "INSERT INTO other VALUES(1)");
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01", y = 1 } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? sql);
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo(rows);
    }

    [Test]
    [Arguments("LEFT", "[[1],[2]]")]
    [Arguments("RIGHT", "[[2],[3]]")]
    public async Task Merged_outer_join_columns_use_the_preserved_side(string side, string rows)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE a(n INTEGER)", "INSERT INTO a VALUES(1),(2)",
            "CREATE TABLE b(n INTEGER)", "INSERT INTO b VALUES(2),(3)");
        var response = await SqlApi.ExecuteAsync(api, "SELECT n FROM a " + side + " JOIN b USING(n) ORDER BY n");
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? side);
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo(rows);
    }

    [Test]
    public async Task Aliased_parenthesized_joins_are_refused_instead_of_losing_affinity()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER)", "INSERT INTO t VALUES(1)");
        var response = await SqlApi.SendAsync(api, api.Client, new
        {
            sql = "SELECT count(*) FROM (t JOIN (SELECT 1 AS u) z) q WHERE q.n=:x",
            named = new { x = "01" },
        });
        await Assert.That(SqlApi.Error(response).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(response.Message).IsEqualTo("affinity for aliased parenthesized joins is not supported");
        var healthy = await SqlApi.OkAsync(api, "SELECT count(*) FROM t WHERE n='01'");
        await Assert.That(healthy[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }
}
