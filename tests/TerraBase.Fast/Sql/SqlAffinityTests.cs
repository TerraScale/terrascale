using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityTests
{
    [Test]
    [Arguments("n = :x AND s = :x")]
    [Arguments("n IS :x AND s IS :x")]
    [Arguments(":x = n AND :x = s")]
    [Arguments("n IN (:x, :other) AND s IN (:x, :other)")]
    [Arguments("n >= :x AND n <= :x AND s >= :x AND s <= :x")]
    public async Task Repeated_parameters_keep_comparison_conversions_local_and_projections_original(string predicate)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (n INTEGER, s TEXT)",
            "INSERT INTO t VALUES (1, '01'), (2, '02'), (1, '1'), (1.25, '1.25'), ('no', 'no'), (NULL, NULL)");
        var sql = "SELECT :x, typeof(:x), n, s FROM t WHERE " + predicate;
        (object? Value, string Rows)[] cases =
        [
            ("01", """[["01","text",1,"01"]]"""),
            ("02", """[["02","text",2,"02"]]"""),
            (1L, """[[1,"integer",1,"1"]]"""),
            (1.25, """[[1.25,"real",1.25,"1.25"]]"""),
            (true, """[[1,"integer",1,"1"]]"""),
            (null, predicate.Contains(" IS ", StringComparison.Ordinal) ? """[[null,"null",null,null]]""" : "[]"),
            ("no", """[["no","text","no","no"]]"""),
            ("01", """[["01","text",1,"01"]]"""),
        ];
        // Consecutive identical SQL uses the session's cached ParsedSql with different arguments.
        var statements = cases.Select(item => (object)new { sql, named = new { x = item.Value, other = (object?)null } }).ToArray();
        if (!predicate.Contains(":other", StringComparison.Ordinal))
        {
            statements = cases.Select(item => (object)new { sql, named = new { x = item.Value } }).ToArray();
        }

        var response = await SqlApi.SendAsync(api, api.Client, statements);
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? "SQL batch");
        var results = response.Json.GetProperty("results");
        for (var i = 0; i < cases.Length; i++)
        {
            await Assert.That(results[i].GetProperty("rows").GetRawText()).IsEqualTo(cases[i].Rows);
        }
    }
}
