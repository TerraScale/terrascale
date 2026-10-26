using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13;

[ClassDataSource<DashboardRootFixture>]
public sealed class AnalyticsPageAuditJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("/analytics?range=1h")]
    [Arguments("/?range=1h")]
    public async Task LargeCountsRenderCompactValuesWithExactTitles(string route)
    {
        var producer = new AuditAnalyticsProducer { Mode = "large" };
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, producer.RespondAsync);
        using var client = scenario.CreateClient();
        producer.DatabaseId = await CreateDatabaseAsync(client);
        var html = await GetPageAsync(client, route);
        foreach (var (label, compact, exact) in new[] { ("Requests", "5.6B", "5,612,334,001"), ("Errors", "3.4M", "3,400,000"), ("Operations", "7.8T", "7,800,000,000,000") })
            await Assert.That(html).Contains($"<dt class=\"ts-stat-label\">{label}</dt><dd class=\"ts-stat-value\" title=\"{exact}\">{compact}</dd>");
        await Assert.That(html).Contains("5,612,334,001").Because("request chart tooltips keep the exact grouped count");
    }

    [Test]
    [Arguments("duplicate", "/analytics?range=1h", "Could not load analytics measurements.")]
    [Arguments("duplicate", "/?range=1h", "Analytics measurements could not be loaded.")]
    public async Task DuplicateRowsAreRejected(string mode, string route, string expectedError)
    {
        var producer = new AuditAnalyticsProducer();
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, producer.RespondAsync);
        using var client = scenario.CreateClient();
        producer.DatabaseId = await CreateDatabaseAsync(client);
        var baseline = await GetPageAsync(client, route);
        await SaveEvidenceAsync("single" + (route.StartsWith("/analytics", StringComparison.Ordinal) ? "-analytics" : "-home"), route, producer, baseline);
        await Assert.That(baseline).Contains("<dt class=\"ts-stat-label\">Requests</dt><dd class=\"ts-stat-value\" title=\"10\">10</dd>");
        await Assert.That(baseline).Contains("<dt class=\"ts-stat-label\">Storage</dt><dd class=\"ts-stat-value\">4.0 KiB</dd>");
        producer.Mode = mode;
        var html = await GetPageAsync(client, route);
        await SaveEvidenceAsync(mode + (route.StartsWith("/analytics", StringComparison.Ordinal) ? "-analytics" : "-home"), route, producer, html);
        await Assert.That(producer.Reads).IsGreaterThan(0);
        await Assert.That(html).Contains(expectedError)
            .Because("An ambiguous duplicate must not become measured totals.");
    }

    [Test]
    public async Task HistoryLowercaseRegionLinksAndAPartialLatestBucketStayMeasured()
    {
        var producer = new AuditAnalyticsProducer { Mode = "foreign-region" };
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, producer.RespondAsync);
        using var client = scenario.CreateClient();
        producer.DatabaseId = await CreateDatabaseAsync(client);
        var history = await GetPageAsync(client, "/analytics?range=1h");
        await Assert.That(history).Contains("<dt class=\"ts-stat-label\">Requests</dt><dd class=\"ts-stat-value\" title=\"10\">10</dd>")
            .Because("TerraBase keeps observations for a region the database has left.");
        await Assert.That(history).Contains("text-anchor=\"end\">15</text>").Because("10 requests get round grid steps 0, 5, 10, 15, 20.");
        producer.Mode = "latest-partial";
        var partial = await GetPageAsync(client, "/analytics?range=1h&region=gru");
        await Assert.That(partial).Contains("value=\"GRU\" selected=\"selected\"").Because("Region cards link with lowercase codes.");
        await Assert.That(partial).Contains("<dt class=\"ts-stat-label\">Storage</dt><dd class=\"ts-stat-value\">4.0 KiB</dd>");
        await Assert.That(partial.Contains("<dd class=\"ts-stat-value\">Not measured</dd>", StringComparison.Ordinal)).IsFalse()
            .Because("An idle, unsampled current minute must not hide the previous minute's storage.");
    }

    [Test]
    public async Task AnalyticsDoesNotDrawLatencyAcrossAnUnreportedMinute()
    {
        // The docs promise latency gaps; absent-bucket versus zero-traffic semantics still need producer confirmation.
        var producer = new AuditAnalyticsProducer { Mode = "explicit-gap" };
        await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, producer.RespondAsync);
        using var client = scenario.CreateClient();
        // The longest database name the API accepts (63 characters) makes the longest latency series names.
        var name = "audit-latency-" + new string('x', 44) + "-tail";
        producer.DatabaseId = await CreateDatabaseAsync(client, name);
        const string route = "/analytics?range=1h";
        var explicitGap = await GetPageAsync(client, route);
        await SaveEvidenceAsync("explicit-null-gap-analytics", route, producer, explicitGap);
        var explicitChart = Regex.Match(explicitGap, "<svg class=\"ts-chart ts-analytics-plot\"[^>]+aria-label=\"Approximate request latency[^>]*>(.*?)</svg>\\s*</div>", RegexOptions.Singleline).Groups[1].Value;
        await Assert.That(explicitChart).Contains("Not reported");
        foreach (Match path in Regex.Matches(explicitChart, "<path class=\"ts-chart-line[^\"]*\" d=\"([^\"]*)\""))
            await Assert.That(path.Groups[1].Value.Contains('C') || path.Groups[1].Value.Contains('L')).IsFalse();
        producer.Mode = "sparse";
        var html = await GetPageAsync(client, route);
        await SaveEvidenceAsync("sparse-analytics", route, producer, html);
        var chart = Regex.Match(html, "<svg class=\"ts-chart ts-analytics-plot\"[^>]+aria-label=\"Approximate request latency[^>]*>(.*?)</svg>\\s*</div>", RegexOptions.Singleline).Groups[1].Value;
        await Assert.That(chart.Length).IsGreaterThan(0);
        var columns = Regex.Matches(chart, "class=\"ts-chart-guide\" x1=\"([0-9.]+)%\".*?<rect class=\"ts-chart-tip-box\" x=\"(-?[0-9.]+)\" y=\"[^\"]*\" width=\"([0-9.]+)\"", RegexOptions.Singleline)
            .Select(match => match.Groups.Values.Skip(1).Select(group => double.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
        await Assert.That(columns.Length).IsEqualTo(Regex.Matches(chart, "class=\"ts-chart-column\"").Count).Because("Every hover column draws a guide and a tooltip.");
        await Assert.That(columns.Length).IsGreaterThan(0);
        foreach (var (guide, left, width) in columns.Select(values => (values[0], values[1], values[2])))
        {
            await Assert.That(guide is >= 0 and <= 100).IsTrue().Because("Hover columns stay inside the plot width.");
            // At a 360px viewport the plot is 272px wide, with a 48px y-axis gutter on the left and 8px on the right.
            var plot = 272 * guide / 100;
            await Assert.That(plot + left >= -48 - 0.2 && plot + left + width <= 272 + 8 + 0.2).IsTrue()
                .Because("The whole tooltip stays inside the chart frame at the narrowest supported width, without horizontal overflow.");
        }
        var visibleNames = Regex.Matches(chart, "class=\"ts-chart-tip-name\"[^>]*>([^<]*)</text>").Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        await Assert.That(visibleNames.Length).IsGreaterThan(0);
        foreach (var visible in visibleNames)
            await Assert.That(visible.StartsWith("p", StringComparison.Ordinal) && visible.EndsWith(" / gru", StringComparison.OrdinalIgnoreCase) && visible.Contains('…') && visible.Length < name.Length)
                .IsTrue().Because("Long series names are shortened in the middle, keeping the percentile and region readable.");
        await Assert.That(chart.Contains(": p50 " + name + " / gru ", StringComparison.OrdinalIgnoreCase)).IsTrue()
            .Because("Assistive technology still reads the full series name for each column.");
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
        await Assert.That(html).Contains("title=\"9,007,199,254,740,993\"");
        await Assert.That(html).Contains("Not measured");
        await Assert.That(html).Contains("Not reported");
        await Assert.That(html).Contains("<span class=\"ts-mono\">gru</span></a></th><td class=\"ts-num\" title=\"9,007,199,254,740,993\">9007.2T</td><td class=\"ts-num\">Not reported</td>")
            .Because("One gru bucket sums its request counter exactly and has no latest p95 to report.");
        await Assert.That(html).Contains(producer.From.UtcDateTime.ToString("MMM d HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(html.Contains("Could not load analytics measurements.", StringComparison.Ordinal)).IsFalse();
        await Assert.That(producer.Reads).IsEqualTo(2);
        var reads = producer.Reads;
        using var foreign = await client.GetAsync("/analytics?range=1h&database=not-in-this-project");
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(producer.Reads).IsEqualTo(reads).Because("Unauthorized database filters are rejected before producer reads.");
    }

    private static async Task<string> CreateDatabaseAsync(HttpClient client, string name = "audit-metrics")
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
        { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
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
        var stats = Regex.Matches(html, "<dt class=\"ts-stat-label\">([^<]+)</dt><dd class=\"ts-stat-value\"[^>]*>([^<]*)</dd>")
            .Select(match => new { label = match.Groups[1].Value, value = match.Groups[2].Value }).ToArray();
        var charts = Regex.Matches(html, "<svg class=\"ts-chart ts-analytics-plot\"[^>]+aria-label=\"([^\"]+)\"[^>]*>(.*?)</svg>\\s*</div>", RegexOptions.Singleline)
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
