using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Bogus;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Shared.Formatting;

namespace TerraScale.Dashboard.E2E.Features.Analytics;

[ClassDataSource<DashboardRootFixture>]
public sealed class AnalyticsValuesJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("1h", false)]
    [Arguments("1h", true)]
    [Arguments("24h", true)]
    [Arguments("7d", true)]
    [Arguments("30d", true)]
    public async Task EveryViewPreservesCountersGaugesDirectionsAndHistoricalRegions(string range, bool senderReports)
    {
        var databases = new List<(string Id, string Name, long Scale)>();
        var faker = new Faker();
        var reads = 0;
        var omitOldStorage = false;
        var (bucket, duration) = range switch { "1h" => (60, 3600), "24h" => (3600, 86400), "7d" => (21600, 604800), _ => (86400, 2592000) };
        var to = DateTimeOffset.FromUnixTimeSeconds((DateTimeOffset.UtcNow.ToUnixTimeSeconds() / bucket + 1) * bucket);
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root,
            configureScenario: app => app.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements, TerraBaseHomeMeasurements>()), producerExtension: http =>
        {
            var path = http.Request.Path.Value!;
            if (databases.Count != 2 || !(path.EndsWith("/analytics", StringComparison.Ordinal) || path.EndsWith("/replication", StringComparison.Ordinal)))
                return Task.FromResult<IResult?>(null);
            Interlocked.Increment(ref reads);
            var projectId = path.Split('/')[3];
            var items = databases.Where(db => !http.Request.Query.ContainsKey("database") || http.Request.Query["database"] == db.Id)
                .SelectMany(db => new[] { "gru", "sjc", "fra" }.Select((region, index) =>
                {
                    var scale = db.Scale * (index + 1);
                    object Sample(int back) => new
                    {
                        start = to.AddSeconds(-back * bucket).ToOffset(TimeSpan.FromHours(-3)),
                        requests = new { get = back == 2 ? scale : 0, post = back == 2 ? 2 * scale : 0 },
                        operations = new { read = back == 2 ? 4 * scale : 0, put = back == 2 ? 5 * scale : 0, patch = back == 2 ? 6 * scale : 0, delete = back == 2 ? 7 * scale : 0 },
                        errors = back == 2 ? scale : 0,
                        latency = new { p50Ms = back == 2 ? .125 * scale : (double?)null, p95Ms = back == 2 ? 1.125 * scale : (double?)null, p99Ms = back == 2 ? 2.125 * scale : (double?)null },
                        storageBytes = back == 2 ? 1024 * scale : (long?)null,
                        clientBandwidth = new { bytesSent = back == 2 ? 13 * scale : 0, bytesReceived = back == 2 ? 17 * scale : 0 },
                        replicationBandwidth = new { bytesSent = back == 2 ? 8 * scale : 0, bytesReceived = back == 2 ? 9 * scale : 0 }
                    };
                    object Meter(string kind, long sent, long received) => new { sourceRegion = "gru", destinationRegion = "sjc", kind, bytesSent = sent * db.Scale, bytesReceived = received * db.Scale };
                    return path.EndsWith("/analytics", StringComparison.Ordinal)
                        ? (object)new { databaseId = db.Id, region, series = omitOldStorage && region == "fra" ? new[] { Sample(1) } : new[] { Sample(2), Sample(1) } }
                        : new
                        {
                            databaseId = db.Id,
                            databaseName = db.Name,
                            region,
                            status = "healthy",
                            replicatedOps = scale,
                            lagMs = (double?)null,
                            divergence = (bool?)null,
                            convergenceMs = (double?)null,
                            lastApplied = (DateTimeOffset?)null,
                            bandwidth = region == "gru" ? new[] { Meter("delta", senderReports ? 100 : 0, 0), Meter("anti_entropy", senderReports ? 20 : 0, 0) }
                                : region == "sjc" ? new[] { Meter("delta", 0, 100), Meter("anti_entropy", 0, senderReports ? 0 : 20) } : [],
                            series = Array.Empty<object>()
                        };
                }).Where(item => !path.EndsWith("/analytics", StringComparison.Ordinal) || !http.Request.Query.ContainsKey("region") ||
                    JsonSerializer.SerializeToElement(item).GetProperty("region").GetString() == http.Request.Query["region"])).ToArray();
            return Task.FromResult<IResult?>(Results.Json(new { projectId, range, from = to.AddSeconds(-duration), to, bucketSeconds = bucket, items }));
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var producer = scenario.CreateProviderClient();
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        using (var plan = await producer.PostAsJsonAsync("/__test/control", new
        {
            command = "plan",
            organizationId = identity.RootElement.GetProperty("organizationId").GetString(),
            value = OrganizationRecord.StarterPlanId
        }))
            await Assert.That(plan.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        foreach (var scale in new long[] { 1, 10 })
        {
            var name = "metrics-" + faker.Random.AlphaNumeric(10).ToLowerInvariant();
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/databases"), ["name"] = name, ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            databases.Add((body.RootElement.GetProperty("id").GetString()!, name, scale));
        }

        foreach (var database in new[] { "", databases[0].Id, databases[1].Id })
            foreach (var region in new[] { "", "gru", "sjc", "fra" })
            {
                var before = reads;
                var html = await Page("/analytics?range=" + range + "&database=" + database + "&region=" + region);
                var scale = (database == "" ? 11 : database == databases[0].Id ? 1 : 10) * (region switch { "gru" => 1, "sjc" => 2, "fra" => 3, _ => 6 });
                await Stat(html, "Requests", (3 * scale).ToString());
                await Stat(html, "Errors", scale.ToString());
                await Stat(html, "Operations", (22 * scale).ToString());
                await Stat(html, "Storage", scale + ".0 KiB");
                await Stat(html, "Client bandwidth", MetricFormat.Bytes(30 * scale));
                await Stat(html, "Replication sent", MetricFormat.Bytes(8 * scale));
                await Stat(html, "Replication received", MetricFormat.Bytes(9 * scale));
                await Assert.That(html).Contains(" UTC: Sent " + (13 * scale).ToString("N0", CultureInfo.InvariantCulture) + ", Received " + (17 * scale).ToString("N0", CultureInfo.InvariantCulture));
                foreach (var db in databases.Where(db => database == "" || db.Id == database))
                    foreach (var code in new[] { "gru", "sjc", "fra" }.Where(code => region == "" || code == region))
                        foreach (var percentile in new[] { ("p50", 125), ("p95", 1125), ("p99", 2125) })
                        {
                            var sampleScale = db.Scale * (code == "gru" ? 1 : code == "sjc" ? 2 : 3);
                            await Assert.That(html).Contains(percentile.Item1 + " " + db.Name + " / " + code.ToUpperInvariant() + " " + (percentile.Item2 * sampleScale).ToString("N0", CultureInfo.InvariantCulture));
                        }
                await Assert.That(reads - before).IsEqualTo(1).Because("one regional endpoint already contains every observing region");
                await Assert.That(html).Contains(to.AddSeconds(-bucket).UtcDateTime.ToString("MMM d HH:mm 'UTC'"));
                if (region == "") await Assert.That(html).Contains("value=\"FRA\"").Because("observed history outside placement remains filterable");
            }

        var fullHome = await Page("/?range=" + range);
        var home = Section(fullHome, "analytics-summary");
        await Stat(home, "Requests", "198");
        await Stat(home, "Storage", "66.0 KiB");
        await Stat(home, "Worst p95 latency", "33.8 ms");
        await Stat(home, "Replication bandwidth", "1.3 KiB");
        await Stat(Section(fullHome, "home-replication"), "Replication bandwidth", "1.3 KiB");
        foreach (var path in new[] { "/usage?range=" + range, "/databases/" + databases[0].Id + "/replication?range=" + range })
        {
            var html = await Page(path);
            var scale = path.StartsWith("/usage", StringComparison.Ordinal) ? 11 : 1;
            await Stat(html, "Replicated ops", (6 * scale).ToString());
            await Stat(html, "Replication bandwidth", scale == 11 ? "1.3 KiB" : "120 B");
            await Stat(html, "Replication sent", MetricFormat.Bytes(senderReports ? 120 * scale : 0));
            await Stat(html, "Replication received", MetricFormat.Bytes((senderReports ? 100 : 120) * scale));
            await Assert.That(html).Contains("Not reported").Because("null lag and convergence are not zeros");
            await Assert.That(html).Contains("Wire bytes");
        }
        var detail = await Page("/databases/" + databases[0].Id + "?range=" + range);
        await Assert.That(detail).Contains("fra 12,");
        await Assert.That(detail).Contains("fra 54,");
        await Assert.That(detail).Contains("fra 3,072,");
        await Assert.That(detail).Contains("fra 3,375,");
        await Assert.That(detail).Contains("fra Not reported,");

        omitOldStorage = true;
        await Stat(await Page("/analytics?range=" + range), "Storage", "Not measured");

        async Task<string> Page(string path)
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(path);
            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        }
        static string Section(string html, string id)
        {
            var start = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
            return html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        }
        static async Task Stat(string html, string label, string value)
        {
            var match = Regex.Match(html, "<dt class=\"ts-stat-label\">" + Regex.Escape(label) + "</dt><dd class=\"ts-stat-value\"([^>]*)>([^<]*)</dd>");
            var exact = Regex.Match(match.Groups[1].Value, "title=\"([^\"]+)\"");
            var actual = long.TryParse(value, out _) && exact.Success ? exact.Groups[1].Value.Replace(",", "") : match.Groups[2].Value;
            await Assert.That(actual).IsEqualTo(value).Because(label);
        }
    }
}
