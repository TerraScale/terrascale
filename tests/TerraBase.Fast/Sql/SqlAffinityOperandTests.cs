using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityOperandTests
{
    [Test]
    [Arguments("(n) = (:x) AND (s) = (:x)", 1)]
    [Arguments("(:x) = (n) AND (:x) = (s)", 1)]
    [Arguments("(n) IN ((:x)) AND (s) IN ((:x))", 1)]
    [Arguments("n BETWEEN :x AND :x AND s BETWEEN :x AND :x", 1)]
    [Arguments("(n) BETWEEN (:x) AND (:x) AND (s) BETWEEN (:x) AND (:x)", 1)]
    [Arguments("n NOT BETWEEN :x AND :x", 0)]
    [Arguments("+n = :x", 0)]
    [Arguments("(n + 0) = :x", 0)]
    [Arguments("+n BETWEEN :x AND :x", 0)]
    public async Task Parentheses_and_between_keep_column_affinity_while_unary_plus_and_arithmetic_do_not(string predicate, int count)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (n INTEGER, s TEXT)", "INSERT INTO t VALUES (1, '01')");
        var response = await SqlApi.SendAsync(api, api.Client,
            new { sql = "SELECT count(*), :x, typeof(:x) FROM t WHERE " + predicate, named = new { x = "01" } },
            new { sql = "SELECT count(*), :x, typeof(:x) FROM (SELECT (n) FROM t) q WHERE q.n = :x", named = new { x = "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? predicate);
        var results = response.Json.GetProperty("results");
        await Assert.That(results[0].GetProperty("rows").GetRawText()).IsEqualTo($"[[{count},\"01\",\"text\"]]");
        await Assert.That(results[1].GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"01","text"]]""");
    }
}
