using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlRegressionTests
{
    [Test]
    public async Task Default_values_generate_keys_apply_defaults_and_return_the_stored_row()
    {
        await using var api = await DocumentApi.StartAsync();
        var results = await SqlApi.OkAsync(api,
            "CREATE TABLE t (id INTEGER PRIMARY KEY, label TEXT NOT NULL DEFAULT 'new', n INTEGER DEFAULT 7, r REAL DEFAULT 1.25, optional TEXT)",
            "INSERT INTO t DEFAULT VALUES RETURNING *");
        var inserted = results[1];
        var row = inserted.GetProperty("rows")[0];
        var key = inserted.GetProperty("last_insert_rowid").GetInt64();
        await Assert.That(key).IsGreaterThan(0).And.IsLessThan(1L << 53);
        await Assert.That(inserted.GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(row.GetRawText()).IsEqualTo($"[{key},\"new\",7,1.25,null]");
        var stored = await SqlApi.OkAsync(api, "SELECT * FROM t");
        await Assert.That(stored[0].GetProperty("rows")[0].GetRawText()).IsEqualTo(row.GetRawText());

        await SqlApi.OkAsync(api, "CREATE TABLE required (id INTEGER PRIMARY KEY, v TEXT NOT NULL)");
        var refused = await SqlApi.ExecuteAsync(api, "INSERT INTO required DEFAULT VALUES");
        await Assert.That(SqlApi.Error(refused).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_NOTNULL");
        var empty = await SqlApi.OkAsync(api, "SELECT count(*) FROM required");
        await Assert.That(empty[0].GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
    }

    [Test]
    [Arguments("REAL", "1e999", false)]
    [Arguments("REAL", "-1e999", false)]
    [Arguments("REAL", "'1e999'", true)]
    [Arguments("NUMERIC", "'1e999'", true)]
    [Arguments("INTEGER", "'1e999'", true)]
    public async Task Defaults_refuse_nonfinite_numbers_and_keep_overflowing_numeric_text_as_text(string affinity, string fallback, bool accepted)
    {
        await using var api = await DocumentApi.StartAsync();
        var response = await SqlApi.ExecuteAsync(api,
            $"CREATE TABLE t (v {affinity} DEFAULT {fallback})", "INSERT INTO t DEFAULT VALUES RETURNING v");
        if (!accepted)
        {
            // Numeric defaults are already checked by the DDL parser, before any table or row is committed.
            await Assert.That(SqlApi.Error(response)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 0));
            var catalog = await SqlApi.OkAsync(api, "SELECT count(*) FROM sqlite_master WHERE name = 't'");
            await Assert.That(catalog[0].GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
            return;
        }

        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Json.GetProperty("results")[1].GetProperty("rows").GetRawText()).IsEqualTo("""[["1e999"]]""");
        var stored = await SqlApi.OkAsync(api, "SELECT v, typeof(v) FROM t");
        await Assert.That(stored[0].GetProperty("rows").GetRawText()).IsEqualTo("""[["1e999","text"]]""");
    }

    [Test]
    [Arguments("SELECT 1e999")]
    [Arguments("SELECT -1e999")]
    [Arguments("SELECT 1e308 * 1e308")]
    [Arguments("INSERT INTO t VALUES (2, 1), (3, 1e999) RETURNING *")]
    [Arguments("UPDATE t SET v = 1e308 * 1e308 RETURNING v")]
    public async Task Nonfinite_results_refuse_the_statement_and_roll_back_the_entire_http_batch(string sql)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, v REAL)", "INSERT INTO t VALUES (1, 1.25)");

        var refused = await SqlApi.ExecuteAsync(api, "INSERT INTO t VALUES (4, 2.5)", sql);

        await Assert.That(SqlApi.Error(refused)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 1));
        await Assert.That(refused.Message).IsEqualTo("nonfinite numbers are not supported");
        var stored = await SqlApi.OkAsync(api, "SELECT id, v FROM t ORDER BY id", "SELECT 1e308");
        await Assert.That(stored[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1,1.25]]");
        await Assert.That(stored[1].GetProperty("rows")[0][0].GetDouble()).IsEqualTo(1e308);
    }

    [Test]
    public async Task Nonfinite_http_arguments_are_refused_before_any_statement_runs()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (v REAL)");
        using var number = JsonDocument.Parse("1e999");
        var refused = await SqlApi.SendAsync(api, api.Client,
            new { sql = "INSERT INTO t VALUES (1)" },
            new { sql = "INSERT INTO t VALUES (?)", args = new[] { number.RootElement } });

        await Assert.That((refused.Status, refused.Code, refused.Op)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request", 1));
        var stored = await SqlApi.OkAsync(api, "SELECT count(*) FROM t");
        await Assert.That(stored[0].GetProperty("rows").GetRawText()).IsEqualTo("[[0]]");
    }
}
