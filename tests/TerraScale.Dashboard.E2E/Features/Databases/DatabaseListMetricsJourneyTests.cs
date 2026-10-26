using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseListMetricsJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task DatabaseListReadsProjectAnalyticsOnceAndKeepsMissingMetricsMissing()
    {
        var measuredId = "";
        var currentId = "";
        var queries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method != "GET" || currentId.Length == 0 || !http.Request.Path.Value!.EndsWith("/analytics", StringComparison.Ordinal))
                return Task.FromResult<IResult?>(null);
            queries.Enqueue(http.Request.QueryString.Value ?? "");
            var projectId = http.Request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
            const int bucket = 21600;
            var to = DateTimeOffset.FromUnixTimeSeconds((DateTimeOffset.UtcNow.ToUnixTimeSeconds() / bucket + 1) * bucket);
            object Bucket(int back, object operations, object latency, long? storageBytes) => new
            {
                start = to.AddSeconds(-back * bucket),
                requests = new { GET = 10L },
                operations,
                errors = 0L,
                latency,
                storageBytes,
                clientBandwidth = new { bytesSent = 13L, bytesReceived = 17L },
                replicationBandwidth = new { bytesSent = 0L, bytesReceived = 0L }
            };
            // The measured database's latest bucket reports explicit null storage and latency, so its older gauges must not resurface.
            // The current database reports gauges in the latest bucket, which win over its older, larger readings.
            var items = new object[] { new
            {
                databaseId = measuredId,
                region = "gru",
                series = new[]
                {
                    Bucket(2, new { read = 5_000_000_000L, put = 3L, patch = 1L }, new { p50Ms = 1d, p95Ms = 2.5d, p99Ms = 3d }, 4096),
                    Bucket(1, new { delete = 2L }, new { p50Ms = (double?)null, p95Ms = (double?)null, p99Ms = (double?)null }, null)
                }
            }, new
            {
                databaseId = currentId,
                region = "gru",
                series = new[]
                {
                    Bucket(2, new { read = 1L }, new { p50Ms = 1d, p95Ms = 9d, p99Ms = 10d }, 1024),
                    Bucket(1, new { read = 2L }, new { p50Ms = 1d, p95Ms = 3.5d, p99Ms = 4d }, 8192)
                }
            } };
            return Task.FromResult<IResult?>(Results.Json(new { projectId, range = "7d", from = to.AddSeconds(-604800), to, bucketSeconds = bucket, items }));
        });
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        using (var plan = await producer.PostAsJsonAsync("/__test/control", new
        {
            command = "plan",
            organizationId = identity.RootElement.GetProperty("organizationId").GetString()!,
            value = TerraScale.Dashboard.Shared.Accounts.OrganizationRecord.StarterPlanId
        }))
            await Assert.That(plan.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        foreach (var name in new[] { "measured-list", "quiet-list", "current-list" })
        {
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            if (name == "measured-list") measuredId = body.RootElement.GetProperty("id").GetString()!;
            if (name == "current-list") currentId = body.RootElement.GetProperty("id").GetString()!;
        }

        using var response = await client.GetAsync("/databases?q=list&range=7d");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        await Assert.That(queries.ToArray()).IsEquivalentTo(new[] { "?range=7d" }).Because("one project-scoped read serves every row");
        await Assert.That(html).Contains("href=\"/databases?q=list&range=30d#project-databases\"").Because("range links keep the search filter");
        await Assert.That(html).Contains("aria-current=\"page\">7d</a>");
        await Assert.That(html).Contains("href=\"/databases?range=7d#project-databases\">Clear</a>").Because("clearing the search keeps the range");

        var measured = Row(html, "measured-list");
        await Assert.That(measured).Contains(">5.0B</span>");
        await Assert.That(measured).Contains("title=\"5,000,000,000 reads in 7d\"").Because("large counters keep every digit");
        await Assert.That(measured).Contains("title=\"6 puts, patches and deletes in 7d\"");
        await Assert.That(measured).Contains("<title>Reads trend, 7d</title>");
        await Assert.That(measured.Split("Not reported").Length - 1).IsEqualTo(2).Because("explicit null gauges in the latest bucket stay missing");
        await Assert.That(measured).DoesNotContain("4.0 KiB").Because("an older storage reading is not current");
        await Assert.That(measured).DoesNotContain("2.5 ms").Because("an older p95 is not current");

        var current = Row(html, "current-list");
        await Assert.That(current).Contains(">8.0 KiB</span>");
        await Assert.That(current).Contains(">3.5 ms</span>");
        await Assert.That(current).Contains("title=\"3 reads in 7d\"");

        var quiet = Row(html, "quiet-list");
        await Assert.That(quiet.Split("Not reported").Length - 1).IsEqualTo(4).Because("a database without measurements never shows zero");
        await Assert.That(quiet).DoesNotContain("<svg class=\"ts-db-spark");

        static string Row(string page, string name)
        {
            var start = page.LastIndexOf("<tr", page.IndexOf(">" + name + "<", page.IndexOf("ts-db-table", StringComparison.Ordinal), StringComparison.Ordinal), StringComparison.Ordinal);
            return page[start..page.IndexOf("</tr>", start, StringComparison.Ordinal)];
        }
    }
}
