using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityCompoundTests
{
    [Test]
    [Arguments("SELECT n AS v FROM t UNION ALL SELECT n FROM t", 2)]
    [Arguments("SELECT n AS v FROM t UNION SELECT n FROM t", 1)]
    [Arguments("SELECT n AS v FROM t INTERSECT SELECT n FROM t", 1)]
    [Arguments("SELECT n AS v FROM t EXCEPT SELECT n FROM t", 0)]
    [Arguments("SELECT (n) AS v FROM t UNION ALL SELECT (n) FROM t", 2)]
    [Arguments("SELECT s AS v FROM t UNION ALL SELECT s FROM t", 2)]
    [Arguments("SELECT +n AS v FROM t UNION ALL SELECT +n FROM t", 0)]
    [Arguments("SELECT n + 0 AS v FROM t UNION ALL SELECT n + 0 FROM t", 0)]
    [Arguments("SELECT b AS v FROM t UNION ALL SELECT +n FROM t", 1)]
    public async Task Compound_results_keep_agreed_column_affinity_and_computed_expressions_keep_none(string body, int count)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (n INTEGER, s TEXT, b BLOB)", "INSERT INTO t VALUES (1, '01', '01')");
        var sql = "SELECT count(*), :x, typeof(:x) FROM (" + body + ") q WHERE (q.v) BETWEEN (:x) AND (:x)";
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? sql);
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText())
            .IsEqualTo($"[[{count},\"01\",\"text\"]]");
    }

    [Test]
    [Arguments("n", "s")]
    [Arguments("s", "n")]
    public async Task Conflicting_compound_affinities_refuse_comparisons_without_blocking_plain_results(string left, string right)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (n INTEGER, s TEXT)", "INSERT INTO t VALUES (1, '01')");
        var body = $"SELECT {left} AS v FROM t UNION ALL SELECT {right} FROM t";
        foreach (var sql in new[] { $"SELECT count(*) FROM ({body}) q WHERE v = :x", $"WITH q AS ({body}) SELECT v AS k FROM q WHERE k = :x" })
        {
            var refused = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
            await Assert.That(SqlApi.Error(refused).Sqlite).IsEqualTo("SQLITE_ERROR");
            await Assert.That(refused.Message).IsEqualTo("compound query output affinity is ambiguous");
        }

        var plain = await SqlApi.OkAsync(api, $"SELECT v FROM ({body}) q ORDER BY typeof(v), v");
        await Assert.That(plain[0].GetProperty("rows").GetRawText()).IsEqualTo("""[[1],["01"]]""");
    }
}
