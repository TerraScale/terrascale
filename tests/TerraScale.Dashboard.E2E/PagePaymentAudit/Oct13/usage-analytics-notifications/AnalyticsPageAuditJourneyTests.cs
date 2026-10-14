using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13;

[ClassDataSource<DashboardRootFixture>]
public sealed class AnalyticsPageAuditJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("duplicate", "/analytics?range=1h", "Could not load analytics measurements.")]
    [Arguments("duplicate", "/?range=1h", "Analytics measurements could not be loaded.")]
    [Arguments("foreign-region", "/analytics?range=1h", "Could not load analytics measurements.")]
    [Arguments("foreign-region", "/?range=1h", "Analytics measurements could not be loaded.")]
    public async Task ContractProposalRejectsDuplicateRowsAndRegionsOutsideCurrentPlacement(string mode, string route, string expectedError)
    {
        // Strict proposed policies: item uniqueness and historical placement are not specified by the current producer contract.
        var producer = new AuditAnalyticsProducer();
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, producer.RespondAsync);
        using var client = scenario.CreateClient();
        producer.DatabaseId = await CreateDatabaseAsync(client);
        var baseline = await GetPageAsync(client, route);
        await SaveEvidenceAsync("single" + (route.StartsWith("/analytics", StringComparison.Ordinal) ? "-analytics" : "-home"), route, producer, baseline);
        await Assert.That(baseline).Contains("<dt class=\"ts-stat-label\">Requests</dt><dd class=\"ts-stat-value\">10</dd>");
        await Assert.That(baseline).Contains("<dt class=\"ts-stat-label\">Storage</dt><dd class=\"ts-stat-value\">4.0 KiB</dd>");
        producer.Mode = mode;
        var html = await GetPageAsync(client, route);
        await SaveEvidenceAsync(mode + (route.StartsWith("/analytics", StringComparison.Ordinal) ? "-analytics" : "-home"), route, producer, html);
        await Assert.That(producer.Reads).IsGreaterThan(0);
        await Assert.That(html).Contains(expectedError)
            .Because("An ambiguous duplicate or a region absent from the database placement must not become measured totals.");
    }

    [Test]
    public async Task AnalyticsDoesNotDrawLatencyAcrossAnUnreportedMinute()
    {
        // The docs promise latency gaps; absent-bucket versus zero-traffic semantics still need producer confirmation.
        var producer = new AuditAnalyticsProducer { Mode = "explicit-gap" };
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, producer.RespondAsync);
        using var client = scenario.CreateClient();
        producer.DatabaseId = await CreateDatabaseAsync(client);
        const string route = "/analytics?range=1h";
        var explicitGap = await GetPageAsync(client, route);
        await SaveEvidenceAsync("explicit-null-gap-analytics", route, producer, explicitGap);
        var explicitChart = Regex.Match(explicitGap, "<svg[^>]+aria-label=\"Approximate request latency[^>]*>(.*?)</svg>", RegexOptions.Singleline).Groups[1].Value;
        await Assert.That(explicitChart).Contains("Not reported");
        foreach (Match path in Regex.Matches(explicitChart, "<path class=\"ts-chart-line[^\"]*\" d=\"([^\"]*)\""))
            await Assert.That(path.Groups[1].Value.Contains('C') || path.Groups[1].Value.Contains('L')).IsFalse();
        producer.Mode = "sparse";
        var html = await GetPageAsync(client, route);
        await SaveEvidenceAsync("sparse-analytics", route, producer, html);
        var chart = Regex.Match(html, "<svg[^>]+aria-label=\"Approximate request latency[^>]*>(.*?)</svg>", RegexOptions.Singleline).Groups[1].Value;
        await Assert.That(chart.Length).IsGreaterThan(0);
        await Assert.That(chart.Contains("Not reported", StringComparison.Ordinal)).IsTrue()
            .Because("The producer omitted the minute between two measured samples; the chart must expose that gap.");
        foreach (Match path in Regex.Matches(chart, "<path class=\"ts-chart-line[^\"]*\" d=\"([^\"]*)\""))
            await Assert.That(path.Groups[1].Value.Contains('C') || path.Groups[1].Value.Contains('L')).IsFalse()
                .Because("Two isolated latency observations must not be connected across missing time.");
    }

    [Test]
    public async Task OffsetBucketsNullGaugesAndLargeCountersRemainTruthfulAfterRetry()
    {
        var producer = new AuditAnalyticsProducer { Mode = "offline" };
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, producer.RespondAsync);
        using var client = scenario.CreateClient();
        producer.DatabaseId = await CreateDatabaseAsync(client);
        var route = "/analytics?range=1h&database=" + producer.DatabaseId + "&region=GRU";
        var unavailable = await GetPageAsync(client, route);
        await Assert.That(unavailable).Contains("Could not load analytics measurements.");
        await Assert.That(unavailable).Contains("value=\"" + producer.DatabaseId + "\" selected=\"selected\"");
        await Assert.That(unavailable).Contains("value=\"GRU\" selected=\"selected\"");
        producer.Mode = "missing";
        var html = await GetPageAsync(client, route);
        await SaveEvidenceAsync("null-offset-large-counter", route, producer, html);
        await Assert.That(html).Contains("9007199254740993");
        await Assert.That(html).Contains("Not measured");
        await Assert.That(html).Contains("Not reported");
        await Assert.That(html).Contains("<span class=\"ts-mono\">gru</span></span></th><td class=\"ts-num\">9007199254740993</td><td class=\"ts-num\">Not reported</td>")
            .Because("One gru bucket sums its request counter exactly and has no latest p95 to report.");
        await Assert.That(html).Contains(producer.From.UtcDateTime.ToString("MMM d HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(html.Contains("Could not load analytics measurements.", StringComparison.Ordinal)).IsFalse();
        await Assert.That(producer.Reads).IsEqualTo(2);
        var reads = producer.Reads;
        using var foreign = await client.GetAsync("/analytics?range=1h&database=not-in-this-project");
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(producer.Reads).IsEqualTo(reads).Because("Unauthorized database filters are rejected before producer reads.");
    }

    private static async Task<string> CreateDatabaseAsync(HttpClient client)
    {
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var step in new Dictionary<string, string>[]
        {
            new() { ["step"] = "account-information", ["displayName"] = "Audit User" },
            new() { ["step"] = "payment-method", ["bypass"] = "1" },
            new() { ["step"] = "project", ["name"] = "Audit project", ["region"] = "GRU" }
        })
        {
            step["_csrf"] = await HttpJourney.CsrfAsync(client, "/onboarding");
            using var advanced = await HttpJourney.PostFormAsync(client, "/onboarding/step", step);
            await Assert.That(advanced.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "audit-metrics", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> GetPageAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task SaveEvidenceAsync(string name, string route, AuditAnalyticsProducer producer, string html)
    {
        var directory = Path.Combine(fixture.Root, "docs/reviews/2026-10-13/usage-analytics-notifications/obj/runtime-evidence", producer.DatabaseId);
        Directory.CreateDirectory(directory);
        var stats = Regex.Matches(html, "<dt class=\"ts-stat-label\">([^<]+)</dt><dd class=\"ts-stat-value\">([^<]*)</dd>")
            .Select(match => new { label = match.Groups[1].Value, value = match.Groups[2].Value }).ToArray();
        var charts = Regex.Matches(html, "<svg[^>]+aria-label=\"([^\"]+)\"[^>]*>(.*?)</svg>", RegexOptions.Singleline)
            .Select(match => new
            {
                label = match.Groups[1].Value,
                columns = Regex.Matches(match.Groups[2].Value, "class=\"ts-chart-column\"").Count,
                paths = Regex.Matches(match.Groups[2].Value, "<path class=\"ts-chart-line[^\"]*\" d=\"([^\"]*)\"")
                    .Select(path => path.Groups[1].Value).ToArray(),
                missingLabels = Regex.Matches(match.Groups[2].Value, "Not reported").Count
            }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            route,
            producer.Mode,
            producer.DatabaseId,
            placement = new[] { "GRU" },
            producer.Reads,
            producerPayload = producer.LastBody,
            stats,
            charts,
            error = html.Contains("Analytics unavailable", StringComparison.Ordinal)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
