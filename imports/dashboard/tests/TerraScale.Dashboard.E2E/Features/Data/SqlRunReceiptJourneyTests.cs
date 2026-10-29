using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
public sealed class SqlRunReceiptJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("ok")]
    [Arguments("lost")]
    [Arguments("malformed")]
    [Arguments("constraint")]
    [Arguments("busy")]
    public async Task BrowserRunsRedirectToAReceiptThatNeverResendsTheStatement(string answer)
    {
        var executions = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (http.Request.Method != "POST" || !http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) return null;
            if (await DatabaseSdkScenario.EmptySqlCatalogAsync(http) is { } catalog) return catalog;
            Interlocked.Increment(ref executions);
            return answer switch
            {
                "lost" => Results.StatusCode(502),
                "malformed" => Results.Json(new { results = Array.Empty<object>() }),
                "constraint" => Results.Json(new { error = new { code = "sql_error", message = "UNIQUE constraint failed: items.id", statement = 0, sqlite_code = "SQLITE_CONSTRAINT_UNIQUE" } }, statusCode: 400),
                "busy" => new RetryAfterResult(),
                _ => Results.Json(new { results = new[] { new { columns = Array.Empty<object>(), rows = Array.Empty<object>(), rows_affected = 1, last_insert_rowid = 7 } } })
            };
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-receipt-" + answer);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        const string sql = "INSERT INTO items (id) VALUES (1)";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = sql }) };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Accept.ParseAdd("text/html");
        using var posted = await client.SendAsync(request);
        await Assert.That(posted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var location = posted.Headers.Location!.ToString();
        await Assert.That(location).StartsWith("/databases/" + id + "/sql?result=");
        // A refresh is this GET repeated; it must render the same outcome and the draft without executing again.
        foreach (var _ in new[] { 1, 2 })
        {
            using var page = await client.GetAsync(location);
            var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
            await Assert.That(html).Contains(sql).Because("The draft survives every outcome.");
            switch (answer)
            {
                case "ok":
                    await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(html).Contains("id=\"sql-result\"");
                    break;
                case "lost" or "malformed":
                    await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                    await Assert.That(html).Contains("Outcome unknown");
                    await Assert.That(html).Contains("SQL outcome unknown: the statement may have run.");
                    break;
                case "constraint":
                    await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                    await Assert.That(html).Contains("UNIQUE constraint failed: items.id (SQLITE_CONSTRAINT_UNIQUE)");
                    break;
                default:
                    await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                    await Assert.That(html).Contains("Retry after 7 s.");
                    break;
            }
        }
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(1);
        using var stale = await client.GetAsync("/databases/" + id + "/sql?result=01J00000000000000000000000");
        await Assert.That(WebUtility.HtmlDecode(await stale.Content.ReadAsStringAsync())).Contains("The statement was not run again.");
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(1);
    }

    private sealed class RetryAfterResult : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = 429;
            http.Response.Headers.RetryAfter = "7";
            return http.Response.WriteAsJsonAsync(new { error = new { code = "rate_limited", message = "too many requests" } });
        }
    }
}
