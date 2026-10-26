using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Logs;

[ClassDataSource<DashboardRootFixture>]
public sealed class LogDrainJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task DrainShipsEachNewRequestOnceAsOtlpWithTheStoredHeader()
    {
        await using var scenario = new AlertScenario(fixture.Root);
        await scenario.StartAsync();
        scenario.Logs.Record(200, DateTimeOffset.UtcNow.AddMinutes(-1));
        await SaveAsync(scenario, HttpStatusCode.SeeOther);
        var page = await LogsPageAsync(scenario);
        await Assert.That(page).Contains("DD-API-KEY: set");
        var removal = System.Text.RegularExpressions.Regex.Match(page, "<dialog id=\"remove-log-drain\"[\\s\\S]*?</dialog>").Value;
        await Assert.That(removal).Contains("Remove log drain: https://otlp.example.com/v1/logs").And.Contains("data-submit-once").And.Contains("deletes the saved endpoint and credentials");
        await Assert.That(page.Contains("dd-secret-value", StringComparison.Ordinal)).IsFalse();
        await scenario.RunDrainsAsync();
        await Assert.That(scenario.Delivery.Requests.Count).IsEqualTo(0);
        var ok = scenario.Logs.Record(200, DateTimeOffset.UtcNow.AddSeconds(1));
        scenario.Logs.Record(404, DateTimeOffset.UtcNow.AddSeconds(2));
        scenario.Delivery.Status = 503;
        await scenario.RunDrainsAsync();
        await Assert.That(await LogsPageAsync(scenario)).Contains("data-drain-error=\"503\"");
        scenario.Delivery.Status = 200;
        await scenario.RunDrainsAsync();
        await scenario.RunDrainsAsync();
        var requests = scenario.Delivery.Requests.ToArray();
        await Assert.That(requests.Length).IsEqualTo(2);
        await Assert.That(requests[1].Body).IsEqualTo(requests[0].Body);
        await Assert.That(requests[1].Url.ToString()).IsEqualTo("https://otlp.example.com/v1/logs");
        await Assert.That(requests[1].Headers["DD-API-KEY"]).IsEqualTo("dd-secret-value");
        using (var body = JsonDocument.Parse(requests[1].Body))
        {
            var resource = body.RootElement.GetProperty("resourceLogs")[0];
            var attributes = resource.GetProperty("resource").GetProperty("attributes").EnumerateArray()
                .ToDictionary(item => item.GetProperty("key").GetString()!, item => item.GetProperty("value").GetProperty("stringValue").GetString());
            await Assert.That(attributes["service.name"]).IsEqualTo("terrascale");
            await Assert.That(attributes["terrascale.database_id"]).IsEqualTo(scenario.DatabaseId);
            await Assert.That(attributes["terrascale.database"]).IsEqualTo("orders");
            // The entry recorded before the drain was saved is never backfilled.
            var records = resource.GetProperty("scopeLogs")[0].GetProperty("logRecords").EnumerateArray().ToArray();
            await Assert.That(records.Length).IsEqualTo(2);
            await Assert.That(records[0].GetProperty("severityText").GetString()).IsEqualTo("INFO");
            await Assert.That(records[1].GetProperty("severityText").GetString()).IsEqualTo("WARN");
            await Assert.That(records[0].GetProperty("body").GetProperty("stringValue").GetString()).IsEqualTo("POST /v1/collections/orders/documents 200 12.5ms");
            await Assert.That(records[0].GetProperty("timeUnixNano").GetString())
                .IsEqualTo((ok.Time.ToUnixTimeMilliseconds() * 1_000_000 + ok.Time.Ticks % TimeSpan.TicksPerMillisecond * 100).ToString(CultureInfo.InvariantCulture));
            await Assert.That(records[1].GetProperty("attributes").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "http.response.status_code")
                .GetProperty("value").GetProperty("intValue").GetString()).IsEqualTo("404");
        }
        scenario.Logs.Record(500, DateTimeOffset.UtcNow.AddSeconds(3));
        await scenario.RunDrainsAsync();
        await Assert.That(scenario.Delivery.Requests.Count).IsEqualTo(3);
        await Assert.That(scenario.Delivery.Requests.Last().Body).Contains("\"severityText\":\"ERROR\"");
        await Assert.That(scenario.Delivery.Requests.Last().Body.Contains("\"intValue\":\"404\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task OnlyOwnersSetAPublicHttpsDrain()
    {
        await using var scenario = new AlertScenario(fixture.Root);
        await scenario.StartAsync();
        await SaveAsync(scenario, (HttpStatusCode)422, "https://192.168.1.10/v1/logs");
        await SaveAsync(scenario, (HttpStatusCode)422, header: "Bad Header");
        scenario.Role("admin");
        await SaveAsync(scenario, HttpStatusCode.Forbidden);
        await Assert.That(await LogsPageAsync(scenario)).Contains("Organization owners manage the log drain.");
        var analytics = await scenario.Client.GetStringAsync("/analytics");
        await Assert.That(analytics).Contains("token_url: https://auth.terrascale.tech/terrascale/oauth/token");
        await Assert.That(analytics).Contains("/metrics");
    }

    // This scenario's TerraBase serves no request logs, so the page answers 503 around an intact drain section.
    private static async Task<string> LogsPageAsync(AlertScenario scenario)
    {
        using var response = await scenario.Client.GetAsync($"/databases/{scenario.DatabaseId}/logs");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task SaveAsync(AlertScenario scenario, HttpStatusCode expected, string url = "https://otlp.example.com/v1/logs", string header = "DD-API-KEY")
    {
        using var response = await scenario.PostAsync("/api/v1/dashboard/log-drain", new()
        { ["databaseId"] = scenario.DatabaseId, ["url"] = url, ["headerName"] = header, ["headerValue"] = "dd-secret-value" });
        await Assert.That(response.StatusCode).IsEqualTo(expected);
    }
}
