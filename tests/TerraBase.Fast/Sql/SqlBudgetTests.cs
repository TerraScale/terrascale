using System.Diagnostics;
using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlBudgetTests
{
    private const string Digits = "(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))";

    private static string Joined(string item, int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"{item} t{i}"));

    // Every evaluated row combination is charged, so a join's fan-out stops at the work budget like a scan does.
    [Test]
    [Arguments("SELECT count(*) FROM {people16}")]
    [Arguments("SELECT t0.id FROM {people16} ORDER BY 1 LIMIT 1")]
    [Arguments("WITH c AS (SELECT id FROM people) SELECT count(*) FROM {cte16}")]
    [Arguments("SELECT count(*) FROM {digits7}")]
    [Arguments("SELECT count(*) FROM people WHERE id IN (SELECT t0.id FROM {people16})")]
    [Arguments("SELECT 1 UNION ALL SELECT count(*) FROM {people16}")]
    public async Task Join_fan_out_past_the_work_budget_is_interrupted(string template)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE people (id INTEGER PRIMARY KEY)", "INSERT INTO people VALUES (1), (2), (3)");
        var sql = template.Replace("{people16}", Joined("people", 16), StringComparison.Ordinal)
            .Replace("{cte16}", Joined("c", 16), StringComparison.Ordinal)
            .Replace("{digits7}", string.Join(", ", Enumerable.Range(0, 7).Select(i => $"{Digits} d{i}")), StringComparison.Ordinal);
        var watch = Stopwatch.StartNew();

        var response = await SqlApi.ExecuteAsync(api, sql);

        await Assert.That(SqlApi.Error(response)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_INTERRUPT", 0));
        await Assert.That(response.Message).IsEqualTo("query too expensive: it exceeds the work budget");
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(6));
    }

    [Test]
    public async Task Small_joins_still_answer_and_joined_subqueries_need_named_columns()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE people (id INTEGER PRIMARY KEY)", "INSERT INTO people VALUES (1), (2), (3)");

        var joined = await SqlApi.OkAsync(api,
            $"SELECT count(*) FROM {Joined("people", 4)}",
            $"SELECT count(*) FROM people p JOIN (SELECT id AS k FROM people) s ON s.k = p.id, {Digits} d",
            "WITH c(n) AS (SELECT id * 2 FROM people) SELECT sum(c.n) FROM c JOIN people p ON p.id = c.n");
        var unnamed = await SqlApi.ExecuteAsync(api, "SELECT count(*) FROM people p, (SELECT id + 1 FROM people) s");

        await Assert.That(string.Join(',', joined.Select(result => result.GetProperty("rows")[0][0].GetInt64()))).IsEqualTo("81,30,2");
        await Assert.That(SqlApi.Error(unnamed).Sqlite).IsEqualTo("SQLITE_ERROR");
    }
}
