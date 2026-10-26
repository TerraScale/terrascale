using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
public sealed class SqlCheckJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("SELECT * FROM items WHERE title = $1 LIMIT 20", true)]
    [Arguments("", true)]
    [Arguments("COMMIT", true)]
    [Arguments("SELECT * FROM items LIMIT 20", false)]
    [Arguments("SELECT FROM", false)]
    public async Task CheckReportsDiagnosticsRetainsSubmittedSqlAndNeverExecutesIt(string sql, bool invalid)
    {
        var executions = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method == "POST" && http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal))
                Interlocked.Increment(ref executions);
            return Task.FromResult<IResult?>(null);
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "sql-check", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = database.RootElement.GetProperty("id").GetString()!;
        var fields = new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["sql"] = sql };
        using var htmlRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql/check") { Content = new FormUrlEncodedContent(fields) };
        htmlRequest.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(htmlRequest);
        await Assert.That(response.StatusCode).IsEqualTo(invalid ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        var submitted = Regex.Match(html, "<textarea[^>]*id=\"sql\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Groups[1].Value;
        await Assert.That(WebUtility.HtmlDecode(submitted)).IsEqualTo(sql);
        await Assert.That(html.Contains("Input checks passed. SQL validity has not been checked.", StringComparison.Ordinal)).IsEqualTo(!invalid);
        await Assert.That(html.Contains("It does not validate SQL syntax", StringComparison.Ordinal)).IsEqualTo(!invalid);
        await Assert.That(html).DoesNotContain("Server check passed.");
        await Assert.That(html).DoesNotContain("Read-only statement accepted.");
        if (sql.Contains("$1", StringComparison.Ordinal)) await Assert.That(html).Contains("cannot bind parameters");
        using var jsonRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql/check") { Content = new FormUrlEncodedContent(fields) };
        jsonRequest.Headers.Accept.ParseAdd("application/json");
        using var jsonResponse = await client.SendAsync(jsonRequest);
        await Assert.That(jsonResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var diagnostics = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync());
        await Assert.That(diagnostics.RootElement.EnumerateArray().Any(item => item.GetProperty("severity").GetString() == "error")).IsEqualTo(invalid);
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(0);
    }

    [Test]
    public async Task ResultsKeepEveryRawCellAndRenderOnlyTwoHundredRows()
    {
        var executions = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method != "POST" || !http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) return Task.FromResult<IResult?>(null);
            Interlocked.Increment(ref executions);
            return Task.FromResult<IResult?>(Results.Json(new
            {
                results = new[] { new
                {
                    columns = new[] { new { name = "n", decltype = "INTEGER" }, new { name = "n", decltype = "TEXT" } },
                    rows = Enumerable.Range(0, 201).Select(_ => new object?[] { 9007199254740993L, null }).ToArray(),
                    rows_affected = 201, last_insert_rowid = (long?)null
                } }
            }));
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-results");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = "INSERT INTO audit VALUES (1) RETURNING *" }) };
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        var payload = Regex.Match(html, "<script[^>]*data-sql-result-data[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        using var json = JsonDocument.Parse(payload.Groups[1].Value);
        await Assert.That(json.RootElement.GetProperty("rows").GetArrayLength()).IsEqualTo(201);
        await Assert.That(json.RootElement.GetProperty("rows")[0][0].GetString()).IsEqualTo("9007199254740993");
        await Assert.That(json.RootElement.GetProperty("rows")[0][1].GetString()).IsEqualTo("null");
        var table = Regex.Match(html, "<tbody>(.*?)</tbody>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Groups[1].Value;
        await Assert.That(Regex.Matches(table, "<tr>", RegexOptions.CultureInvariant).Count).IsEqualTo(200);
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(1);
    }

    [Test]
    public async Task RunExecutesRelationalSqlAndShowsTerraBaseMessages()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-run");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        async Task<(HttpStatusCode Status, JsonElement Body)> RunAsync(string sql)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", new() { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = sql });
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, json.RootElement.Clone());
        }
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new() { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "items" }))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using (var document = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = "create", ["document"] = "{\"title\":\"Ada\"}" }))
            await Assert.That(document.IsSuccessStatusCode).IsTrue();

        var read = await RunAsync("SELECT * FROM items LIMIT 20");
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", read.Body.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("name").GetString()))).IsEqualTo("id,title");
        await Assert.That(read.Body.GetProperty("rows")[0][1].GetString()).IsEqualTo("Ada");
        var missing = await RunAsync("SELECT * FROM nope");
        await Assert.That(missing.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.Body.GetProperty("detail").GetString()).IsEqualTo("no such table: nope");
        var write = await RunAsync("CREATE TABLE things (id TEXT PRIMARY KEY)");
        await Assert.That(write.Status).IsEqualTo(HttpStatusCode.OK).Because("A member with write access runs DDL with a documents:write grant.");
        await Assert.That(write.Body.GetProperty("rowsAffected").GetInt64()).IsEqualTo(1);
        var transaction = await RunAsync("BEGIN");
        await Assert.That(transaction.Status).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(transaction.Body.GetProperty("detail").GetString()).Contains("each run is its own transaction");
    }
}
