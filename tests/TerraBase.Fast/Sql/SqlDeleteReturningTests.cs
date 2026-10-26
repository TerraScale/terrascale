using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlDeleteReturningTests
{
    [Test]
    [Arguments("DELETE FROM t RETURNING *")]
    [Arguments("/* RETURNING */ DELETE /* target */ FROM \"t\" -- RETURNING\n rEtUrNiNg *;")]
    [Arguments("DELETE FROM `t` RETURNING t.id AS id, v")]
    [Arguments("DELETE FROM [t] RETURNING id, v")]
    [Arguments("DELETE FROM main.t RETURNING *")]
    [Arguments("DELETE FROM t AS target RETURNING t.id AS id, v")]
    [Arguments("DELETE FROM t AS \"RETURNING\" RETURNING *")]
    [Arguments("DELETE FROM t AS d WHERE d.id = 1 RETURNING *")]
    [Arguments("DELETE FROM t WHERE v = 'RETURNING' RETURNING *")]
    public async Task Delete_returns_old_values_and_empty_results_keep_the_columns(string sql)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)", "INSERT INTO t VALUES (1, 'RETURNING')");

        var deleted = (await SqlApi.OkAsync(api, sql))[0];
        var empty = (await SqlApi.OkAsync(api, sql))[0];
        var stored = (await SqlApi.OkAsync(api, "SELECT count(*) FROM t"))[0];

        await Assert.That(deleted.GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"RETURNING"]]""");
        await Assert.That(deleted.GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(deleted.GetProperty("columns")[0].GetProperty("name").GetString()).IsEqualTo("id");
        await Assert.That(deleted.GetProperty("columns")[1].GetProperty("name").GetString()).IsEqualTo("v");
        await Assert.That(empty.GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await Assert.That(empty.GetProperty("rows_affected").GetInt64()).IsEqualTo(0);
        await Assert.That(empty.GetProperty("columns").GetRawText()).IsEqualTo(deleted.GetProperty("columns").GetRawText());
        await Assert.That(stored.GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
    }

    [Test]
    public async Task Quoted_returning_identifiers_remain_identifiers()
    {
        await using var api = await DocumentApi.StartAsync();
        var results = await SqlApi.OkAsync(api,
            "CREATE TABLE \"returning\" (id INTEGER PRIMARY KEY, \"returning\" TEXT)",
            "INSERT INTO \"returning\" VALUES (1, 'RETURNING; -- literal')",
            "DELETE FROM \"returning\" RETURNING \"returning\".id, \"returning\".\"returning\" AS \"RETURNING\"");
        await Assert.That(results[2].GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"RETURNING; -- literal"]]""");
        await Assert.That(results[2].GetProperty("columns")[1].GetProperty("name").GetString()).IsEqualTo("RETURNING");
    }

    [Test]
    public async Task Unfiltered_delete_returns_every_row_and_rolls_back_with_a_failed_http_batch()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)", "INSERT INTO t VALUES (1, 'one'), (2, 'two')");
        var failed = await SqlApi.ExecuteAsync(api, "DELETE FROM t RETURNING *", "SELECT missing FROM t");
        await Assert.That(SqlApi.Error(failed)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 1));
        var rows = (await SqlApi.OkAsync(api, "SELECT * FROM t ORDER BY id"))[0];
        await Assert.That(rows.GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"one"],[2,"two"]]""");

        using var reader = await api.ClientWithScopesAsync("documents:read");
        var refused = await SqlApi.ExecuteAsync(api, reader, "DELETE FROM t RETURNING *");
        await Assert.That(SqlApi.Error(refused)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope", "SQLITE_AUTH", 0));
        var deleted = (await SqlApi.OkAsync(api, "DELETE FROM t RETURNING *"))[0];
        await Assert.That(deleted.GetProperty("rows_affected").GetInt64()).IsEqualTo(2);
        await Assert.That(string.Join(',', deleted.GetProperty("rows").EnumerateArray().OrderBy(row => row[0].GetInt64()).Select(row => row.GetRawText())))
            .IsEqualTo("""[1,"one"],[2,"two"]""");
    }

    [Test]
    [Arguments("DELETE FROM t RETURNING")]
    [Arguments("DELETE FROM t RETURNING *; DELETE FROM t")]
    [Arguments("DELETE FROM t RETURNING length(v)")]
    [Arguments("DELETE FROM t RETURNING other.id")]
    public async Task Invalid_returning_does_not_commit_any_statement_in_the_http_batch(string sql)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)", "INSERT INTO t VALUES (1, 'one')");
        var refused = await SqlApi.ExecuteAsync(api, "INSERT INTO t VALUES (2, 'two')", sql);
        await Assert.That(SqlApi.Error(refused)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 1));
        var rows = (await SqlApi.OkAsync(api, "SELECT * FROM t"))[0];
        await Assert.That(rows.GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"one"]]""");
    }
}
