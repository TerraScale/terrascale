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

    [Test]
    public async Task Qualified_columns_and_nested_aliases_resolve_in_their_own_select_scope()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE ints (v INTEGER)", "CREATE TABLE texts (v TEXT)",
            "INSERT INTO ints VALUES (1)", "INSERT INTO texts VALUES ('01')");
        string[] queries =
        [
            "SELECT :x, typeof(:x) FROM ints a JOIN texts b ON a.v = :x AND b.v = :x",
            "SELECT :x, typeof(:x) FROM ints a, texts b WHERE a.v = :x AND b.v = :x",
            "SELECT :x, typeof(:x) FROM ints a, texts b WHERE a.v = '01' AND b.v = '01'",
            "SELECT :x, typeof(:x) FROM ints a WHERE a.v = :x AND EXISTS (SELECT 1 FROM texts a WHERE a.v = :x)",
            "SELECT :x, typeof(:x) FROM texts b WHERE b.v = :x AND EXISTS (SELECT 1 FROM ints a WHERE v = :x)",
            "SELECT :x, typeof(:x) FROM (SELECT v FROM ints WHERE v = :x) a, texts b WHERE b.v = :x",
            "SELECT :x, typeof(:x) FROM texts b WHERE EXISTS (SELECT 1 FROM ints a WHERE b.v = :x AND a.v = :x)",
            "WITH q(k) AS (SELECT v FROM ints) SELECT :x, typeof(:x) FROM q, texts b WHERE q.k = :x AND b.v = :x",
            "SELECT :x, typeof(:x) FROM (SELECT v AS k FROM ints) q, texts b WHERE q.k = :x AND b.v = :x",
        ];
        foreach (var sql in queries)
        {
            var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
            await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? sql);
            await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText())
                .IsEqualTo("""[["01","text"]]""").Because(sql);
        }
    }

    [Test]
    public async Task Numbered_parameters_and_untyped_columns_preserve_the_original_storage_class()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (n NUMERIC, r REAL, s TEXT, b BLOB)",
            "INSERT INTO t VALUES (1.25, 1.25, '1.25', '1.25')");
        var response = await SqlApi.SendAsync(api, api.Client,
            new { sql = "SELECT ?2, typeof(?2), b = ?2 FROM t WHERE n = ?2 AND r = ?2 AND s = ?2", args = new object[] { "unused", "1.25" } },
            new { sql = "SELECT ?1, typeof(?1), b = ?1 FROM t WHERE n = ?1 AND r = ?1 AND s = ?1", args = new object[] { 1.25 } },
            new { sql = "WITH q AS (SELECT '01' AS n FROM t) SELECT count(*) FROM q WHERE n = ?", args = new[] { 1 } },
            new { sql = "SELECT 'it''''s', ?1", args = new[] { "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? "SQL batch");
        var results = response.Json.GetProperty("results");
        await Assert.That(results[0].GetProperty("rows").GetRawText()).IsEqualTo("""[["1.25","text",1]]""");
        await Assert.That(results[1].GetProperty("rows").GetRawText()).IsEqualTo("""[[1.25,"real",0]]""");
        await Assert.That(results[2].GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
        await Assert.That(results[3].GetProperty("rows").GetRawText()).IsEqualTo("""[["it''s","01"]]""");
    }
}
