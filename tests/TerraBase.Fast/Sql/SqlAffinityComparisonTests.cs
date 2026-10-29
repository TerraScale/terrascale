using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityComparisonTests
{
    [Test]
    [Arguments("SELECT count(*) FROM t WHERE n IS NOT DISTINCT FROM :x", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE n IS DISTINCT FROM :x", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE s IS NOT DISTINCT FROM :x", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE s=-1", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE s=+(-1)", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE s IN (-1,+1)", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE s BETWEEN -1 AND -1", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE CAST(n AS INTEGER)=:x", "[[1]]")]
    [Arguments("SELECT count(*) FROM (SELECT CAST(n AS INTEGER) AS k FROM t) q WHERE k=:x", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE (n COLLATE BINARY)=:x", "[[1]]")]
    [Arguments("SELECT (SELECT n FROM t WHERE n=1)=:x, typeof(:x)", """[[1,"text"]]""")]
    [Arguments("SELECT count(*) FROM t WHERE CASE n WHEN :x THEN 1 ELSE 0 END", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE n=n", "[[2]]")]
    [Arguments("SELECT count(*) FROM t WHERE s=CAST(n AS TEXT)", "[[1]]")]
    [Arguments("SELECT count(*) FROM t WHERE +n=:x", "[[0]]")]
    [Arguments("SELECT count(*) FROM t WHERE n+0=:x", "[[0]]")]
    public async Task Comparison_operands_preserve_declared_affinity_and_signed_literals(string sql, string rows)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER,s TEXT)", "INSERT INTO t VALUES(1,'01'),(-1,'-1')");
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? sql);
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo(rows);
    }

    [Test]
    [Arguments("n=s")]
    [Arguments("n=+s")]
    [Arguments("s=+n")]
    [Arguments("n=(s||'')")]
    [Arguments("n IN(s)")]
    [Arguments("s BETWEEN n AND n")]
    [Arguments(":x BETWEEN n AND n")]
    [Arguments("CAST(:x AS TEXT)=n")]
    [Arguments("n IN(SELECT s FROM t)")]
    [Arguments("CASE :x WHEN n THEN 1 ELSE 0 END")]
    public async Task Row_dependent_affinity_conversion_is_explicitly_refused(string predicate)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER,s TEXT)", "INSERT INTO t VALUES(1,'01')");
        var response = await SqlApi.SendAsync(api, api.Client,
            new { sql = "SELECT count(*) FROM t WHERE " + predicate, named = new { x = "01" } });
        await Assert.That(SqlApi.Error(response).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(response.Message).IsEqualTo("comparison requires unsupported row-dependent affinity conversion");
    }

    [Test]
    [Arguments("JOIN b USING(n)")]
    [Arguments("NATURAL JOIN b")]
    public async Task Implicit_join_comparisons_cannot_silently_skip_conversion(string join)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER)", "INSERT INTO t VALUES(1)",
            "CREATE TABLE b(n TEXT)", "INSERT INTO b VALUES('01')");
        var response = await SqlApi.SendAsync(api, api.Client, new { sql = "SELECT count(*) FROM t " + join });
        await Assert.That(SqlApi.Error(response).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(response.Message).IsEqualTo("comparison requires unsupported row-dependent affinity conversion");
    }

    [Test]
    public async Task Signed_integer_boundaries_and_numeric_comparison_parameters_keep_precision()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(s TEXT)", "INSERT INTO t VALUES('-9223372036854775808')");
        var response = await SqlApi.SendAsync(api, api.Client,
            new { sql = "SELECT count(*) FROM t WHERE s=-9223372036854775808" },
            new { sql = "SELECT CAST(9007199254740992 AS REAL)=:x, typeof(:x)", named = new { x = 9007199254740993L } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? "integer boundaries");
        var results = response.Json.GetProperty("results");
        await Assert.That(results[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
        await Assert.That(results[1].GetProperty("rows").GetRawText()).IsEqualTo("""[[0,"integer"]]""");
    }

    [Test]
    public async Task Engine_text_formatting_and_ASCII_numeric_whitespace_match_SQLite()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER,s TEXT)", "INSERT INTO t VALUES(1,'0.0'),(2,'1.0e+20')");
        var response = await SqlApi.SendAsync(api, api.Client,
            new { sql = "SELECT count(*) FROM t WHERE s=-0.0" },
            new { sql = "SELECT count(*) FROM t WHERE s=:x", named = new { x = 1e20 } },
            new { sql = "SELECT count(*) FROM t WHERE n=:x", named = new { x = "\u00a01\u00a0" } },
            new { sql = "SELECT count(*) FROM t WHERE n=:x", named = new { x = " \t1\r\n" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? "comparison formatting");
        var results = response.Json.GetProperty("results");
        await Assert.That(results[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
        await Assert.That(results[1].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
        await Assert.That(results[2].GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
        await Assert.That(results[3].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }

    [Test]
    [Arguments("SELECT count(*) FROM t WHERE (n,s)=(:x,:x)", "row-value comparison affinity is not supported")]
    [Arguments("WITH q AS(SELECT a.n AS k) SELECT count(*) FROM t a WHERE EXISTS(SELECT 1 FROM q WHERE k=:x)", "SQL source column affinity cannot be inferred")]
    public async Task Unknown_or_context_dependent_shapes_are_refused_without_guessing(string sql, string message)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER,s TEXT)", "INSERT INTO t VALUES(1,'01')");
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
        await Assert.That(SqlApi.Error(response).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(response.Message).IsEqualTo(message);
    }

    [Test]
    public async Task Short_IS_syntax_applies_column_affinity()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER)", "INSERT INTO t VALUES(1)");
        var response = await SqlApi.SendAsync(api, api.Client,
            new { sql = "SELECT count(*) FROM t WHERE n IS :x", named = new { x = "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? "short IS comparison");
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }
}
