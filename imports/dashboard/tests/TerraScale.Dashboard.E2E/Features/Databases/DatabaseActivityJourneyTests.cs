using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseActivityJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task OverviewActivityAndReplicationTopologyReadScopedAnalyticsForTheSelectedRange()
    {
        var databaseId = "";
        var measured = 0;
        var queries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method != "GET" || databaseId.Length == 0 || !http.Request.Path.Value!.EndsWith("/analytics", StringComparison.Ordinal))
                return Task.FromResult<IResult?>(null);
            queries.Enqueue(http.Request.QueryString.Value ?? "");
            var projectId = http.Request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
            var range = http.Request.Query["range"].ToString();
            var (bucket, duration) = range switch { "1h" => (60, 3600), "24h" => (3600, 86400), "7d" => (21600, 604800), _ => (86400, 2592000) };
            var to = DateTimeOffset.FromUnixTimeSeconds((DateTimeOffset.UtcNow.ToUnixTimeSeconds() / bucket + 1) * bucket);
            // Values scale with the range so each card proves which range it read.
            var scale = range == "1h" ? 1L : 10L;
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
            var items = Volatile.Read(ref measured) == 0 ? Array.Empty<object>() : new object[] { new
            {
                databaseId,
                region = "gru",
                // TerraBase names committed operations read, put, patch and delete; the second bucket reports no reads, storage or latency.
                series = new[]
                {
                    Bucket(2, new { read = 6 * scale, put = 3 * scale, patch = scale }, new { p50Ms = 1d, p95Ms = 2d, p99Ms = 3d }, 4096),
                    Bucket(1, new { put = 2 * scale }, new { }, null)
                }
            } };
            return Task.FromResult<IResult?>(Results.Json(new { projectId, range, from = to.AddSeconds(-duration), to, bucketSeconds = bucket, items }));
        });
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "measured-activity", ["region"] = "GRU" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            databaseId = body.RootElement.GetProperty("id").GetString()!;
        }
        var path = "/databases/" + databaseId;

        var empty = await PageAsync("?range=1h");
        await Assert.That(Card(empty, "Reads")).Contains("No activity measurements for this database in the last 1h.");
        await Assert.That(Card(empty, "p95 latency (ms)")).Contains("No activity measurements for this database in the last 1h.");
        await Assert.That(Card(empty, "Reads")).Contains("href=\"" + path + "?reads=7d&writes=1h&storage=1h&latency=1h#activity\"");
        await Assert.That(queries.Single()).IsEqualTo("?range=1h&database=" + databaseId);

        Volatile.Write(ref measured, 1);
        var overview = await PageAsync("?reads=1h&writes=7d&storage=1h&latency=1h");
        await Assert.That(queries.Skip(1).ToArray()).IsEquivalentTo(new[] { "?range=1h&database=" + databaseId, "?range=7d&database=" + databaseId })
            .Because("cards sharing a range share one analytics read");
        var reads = Card(overview, "Reads");
        await Assert.That(reads).Contains("href=\"" + path + "?reads=1h&writes=7d&storage=1h&latency=1h#activity\" aria-current=\"page\">1h</a>");
        await Assert.That(reads).Contains(" UTC: gru 6\"");
        await Assert.That(reads).Contains(" UTC: gru 0\"").Because("a reported bucket without the read key has zero reads");
        var writes = Card(overview, "Writes (put, patch, delete)");
        await Assert.That(writes).Contains("aria-current=\"page\">7d</a>");
        await Assert.That(writes).Contains(" UTC: gru 40\"");
        await Assert.That(writes).Contains(" UTC: gru 20\"");
        var storage = Card(overview, "Storage (bytes)");
        await Assert.That(storage).Contains(" UTC: gru 4,096\"");
        await Assert.That(storage).Contains(" UTC: gru Not reported\"");
        await Assert.That(Card(overview, "p95 latency (ms)")).Contains(" UTC: gru 2.00 ms\"");
        await Assert.That(overview.Contains("Activity unavailable", StringComparison.Ordinal)).IsFalse();

        var replication = await PageAsync("/replication?range=1h");
        await Assert.That(replication).Contains("Replication topology");
        await Assert.That(replication.IndexOf("Replication topology", StringComparison.Ordinal)).IsLessThan(replication.IndexOf("id=\"placement\"", StringComparison.Ordinal));
        await Assert.That(replication).Contains("No replication measurements for this database yet.")
            .Because("the topology shows only regions TerraBase reports replication for");
        await Assert.That(queries.Count).IsEqualTo(4);

        using var observations = await producer.GetAsync("/__test/observations");
        using var observed = JsonDocument.Parse(await observations.Content.ReadAsStringAsync());
        await Assert.That(observed.RootElement.GetProperty("failures").GetArrayLength()).IsEqualTo(0);

        static string Card(string html, string title)
        {
            var start = html.IndexOf("aria-label=\"" + title + "\"", StringComparison.Ordinal);
            return start < 0 ? "" : html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        }

        async Task<string> PageAsync(string suffix)
        {
            using var response = await client.GetAsync(path + suffix);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        }
    }
}
