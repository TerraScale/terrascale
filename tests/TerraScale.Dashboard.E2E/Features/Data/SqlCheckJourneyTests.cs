using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
public sealed class SqlCheckJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("SELECT * FROM items WHERE title = $1 LIMIT 20", true)]
    [Arguments("", true)]
    [Arguments("DELETE FROM items", true)]
    [Arguments("SELECT * FROM items LIMIT 20", false)]
    public async Task CheckReportsDiagnosticsRetainsSubmittedSqlAndNeverExecutesIt(string sql, bool invalid)
    {
        var executions = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method == "POST" && http.Request.Path.Value!.EndsWith("/sql", StringComparison.Ordinal))
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
        await Assert.That(html.Contains("Server check passed.", StringComparison.Ordinal)).IsEqualTo(!invalid);
        await Assert.That(html.Contains("Read-only statement accepted.", StringComparison.Ordinal)).IsEqualTo(!invalid);
        if (sql.Contains("$1", StringComparison.Ordinal)) await Assert.That(html).Contains("cannot bind parameters");
        using var jsonRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql/check") { Content = new FormUrlEncodedContent(fields) };
        jsonRequest.Headers.Accept.ParseAdd("application/json");
        using var jsonResponse = await client.SendAsync(jsonRequest);
        await Assert.That(jsonResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var diagnostics = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync());
        await Assert.That(diagnostics.RootElement.EnumerateArray().Any(item => item.GetProperty("severity").GetString() == "error")).IsEqualTo(invalid);
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(0);
    }
}
