using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.DatabaseDataAccess;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseDataAccessJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("SELECT * FROM users WHERE name = $1 LIMIT 20")]
    [Arguments("SELECT * FROM users WHERE name = ? LIMIT 20")]
    public async Task UnboundParametersCannotProduceASuccessfulNativeSqlCheck(string statement)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await CreateDatabaseAsync(client, "oct13-sql-check");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        var fields = new Dictionary<string, string>
        { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = statement };
        using (var diagnostics = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql/check", fields))
        {
            await Assert.That(diagnostics.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await diagnostics.Content.ReadAsStringAsync());
            await Assert.That(json.RootElement.EnumerateArray().Any(item => item.GetProperty("severity").GetString() == "error")).IsTrue();
        }
        using (var run = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", fields))
            await Assert.That(run.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var check = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql/check", fields);
        await Assert.That(check.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var html = WebUtility.HtmlDecode(await check.Content.ReadAsStringAsync());
        await Assert.That(html.Contains("Input checks passed. SQL validity has not been checked.", StringComparison.Ordinal)).IsFalse()
            .Because("The HTML check must agree with its JSON diagnostics and the execution boundary.");
        await Assert.That(html).Contains("The console cannot bind parameters");
    }

    [Test]
    public async Task CancellingANativeQueryAbortsTheProducerAndAllowsANewRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqlCalls = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (http.Request.Method != "POST" || !http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) return null;
            if (await DatabaseSdkScenario.EmptySqlCatalogAsync(http) is { } catalog) return catalog;
            if (Interlocked.Increment(ref sqlCalls) == 1)
            {
                using var cancellation = http.RequestAborted.Register(() => aborted.TrySetResult());
                started.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), http.RequestAborted);
            }
            return Results.Json(new { results = new[] { new { columns = Array.Empty<object>(), rows = Array.Empty<object>(), rows_affected = 0, last_insert_rowid = (long?)null } } });
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await CreateDatabaseAsync(client, "oct13-query-cancel");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        var fields = new Dictionary<string, string>
        { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = "SELECT * FROM users LIMIT 1" };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql")
        { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = client.SendAsync(request, cancellationSource.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();
        var cancelled = false;
        try { using var response = await pending; }
        catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var retry = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", fields);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sqlCalls).IsEqualTo(2);
    }

    internal static async Task<string> CreateDatabaseAsync(HttpClient client, string name)
    {
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static string DocumentTotal(string html)
    {
        var stat = Regex.Match(html, "<div class=\"ts-stat\" data-tone=\"violet\">(?<body>.*?)</div>", RegexOptions.Singleline);
        return Regex.Match(stat.Groups["body"].Value, "<dd class=\"ts-stat-value\"[^>]*>(?<value>[^<]*)</dd>").Groups["value"].Value;
    }
}
