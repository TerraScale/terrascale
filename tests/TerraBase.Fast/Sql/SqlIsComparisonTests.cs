using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlIsComparisonTests
{
    internal const string MatrixSql = """
        SELECT 0 IS 0 AS same_value, 1 IS NOT 0 AS different_value,
         NULL IS NULL, NULL IS 0, 0 IS NULL, NULL IS NOT 0, NULL IS NOT NULL,
         '2' IS 2, 2 IS 2.0, 'text' IS 'text', 'text' IS NOT 'other',
         2 IS TRUE, 2 IS NOT TRUE, 0 IS FALSE, NULL IS FALSE, NULL IS NOT FALSE,
         2 IS (TRUE), 2 IS +TRUE, 2 IS TRUE + 1, TRUE IS 2,
         NULL IS 0 = 0, 0 = NULL IS 0, NULL IS NULL < 1,
         0 IS 0 AND 0, 1 IS 0 OR 1, NOT 0 IS 0, 0 IS 0 IS 1,
         2 IS NOT DISTINCT FROM TRUE, 2 IS DISTINCT FROM TRUE,
         0 IS NOT DISTINCT FROM 0 AND 0, NULL IS DISTINCT FROM NULL OR 1,
         0 BETWEEN 0 AND 2 IS FALSE, 1 | 2 IS 3, 2 * 3 IS 6
        """;
    internal const string MatrixValues = "1,1,1,0,0,1,0,0,1,1,1,1,0,1,0,1,1,0,1,0,1,0,1,0,1,0,1,1,0,0,1,0,1,1";

    [Test]
    public async Task Value_comparisons_preserve_null_truth_tests_and_sqlite_precedence()
    {
        await using var api = await DocumentApi.StartAsync();
        var result = (await SqlApi.OkAsync(api, MatrixSql))[0];
        await Assert.That(result.GetProperty("rows").GetRawText()).IsEqualTo("[[" + MatrixValues + "]]");
        await Assert.That(result.GetProperty("columns")[0].GetProperty("name").GetString()).IsEqualTo("same_value");
        await Assert.That(result.GetProperty("columns")[1].GetProperty("name").GetString()).IsEqualTo("different_value");
    }

    [Test]
    public async Task Parameters_are_values_even_when_bound_to_a_boolean_or_null()
    {
        await using var api = await DocumentApi.StartAsync();
        (object? Left, object? Right, string Rows)[] cases =
        [
            (null, null, "[[1,0,null]]"), (null, 0, "[[0,1,null]]"),
            (2, 2.0, "[[1,0,2]]"), (2, "2", "[[0,1,2]]"),
            (2, true, "[[0,1,2]]"), (0, false, "[[1,0,0]]"),
            ("text", "text", """[[1,0,"text"]]"""),
        ];
        foreach (var (left, right, rows) in cases)
        {
            var result = await SqlApi.SendAsync(api, api.Client, new
            {
                sql = "SELECT :x IS :y AS same, :x IS NOT :y AS different, :x AS original",
                named = new Dictionary<string, object?> { ["x"] = left, ["y"] = right },
            });
            await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo(rows);
        }
    }

    [Test]
    public async Task Column_comparisons_apply_affinity_per_occurrence_without_changing_the_projected_parameter()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, n INTEGER, s TEXT)",
            "INSERT INTO t VALUES (1, 1, '01'), (2, NULL, NULL), (3, 2, '2')");
        var result = await SqlApi.SendAsync(api, api.Client, new
        {
            sql = "SELECT a.id, :x AS original, typeof(:x) AS kind, a.n IS '01', '01' IS a.n, a.n IS NOT '01' "
                + "FROM t AS a WHERE a.n IS :x AND a.s IS :x",
            named = new { x = "01" },
        });
        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(result.Json.GetProperty("results")[0].GetProperty("rows").GetRawText())
            .IsEqualTo("""[[1,"01","text",1,1,0]]""");
        var nulls = (await SqlApi.OkAsync(api, "SELECT a.id, a.n IS a.n, a.n IS NOT a.n FROM t AS a WHERE a.n IS NULL"))[0];
        await Assert.That(nulls.GetProperty("rows").GetRawText()).IsEqualTo("[[2,1,0]]");
        var truth = (await SqlApi.OkAsync(api, "SELECT n IS TRUE, n IS NOT TRUE, n IS FALSE FROM t WHERE id = 3"))[0];
        await Assert.That(truth.GetProperty("rows").GetRawText()).IsEqualTo("[[1,0,0]]");
        var cte = (await SqlApi.OkAsync(api, "WITH c AS (SELECT n AS v FROM t) SELECT v IS '01' FROM c WHERE v IS '01'"))[0];
        await Assert.That(cte.GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }

    [Test]
    [Arguments("SELECT 1 IS")]
    [Arguments("SELECT 1 IS NOT")]
    [Arguments("SELECT 1 IS DISTINCT 1")]
    [Arguments("SELECT 1 IS NOT DISTINCT FROM")]
    [Arguments("SELECT 1 IS 1 END; SELECT 2")]
    public async Task Incomplete_operands_and_unconsumed_tails_are_refused(string sql)
    {
        await using var api = await DocumentApi.StartAsync();
        var response = await SqlApi.ExecuteAsync(api, sql);
        await Assert.That(SqlApi.Error(response)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 0));
    }
}
