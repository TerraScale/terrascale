using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Logs;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseLogJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task Logs_filter_requests_and_show_recent_query_shapes_with_independent_failures()
    {
        var databaseId = "";
        var mode = "records";
        var requests = new ConcurrentQueue<string>();
        var shapeRequests = new ConcurrentQueue<(string Path, string Token, string Pin)>();
        var shapeMode = "records";
        var longShape = "SELECT * FROM widgets WHERE " + new string('x', 120) + " = ? <script>alert(1)</script>";
        var seen = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (databaseId.Length > 0 && http.Request.Path.Value!.EndsWith("/query-shapes", StringComparison.Ordinal))
            {
                shapeRequests.Enqueue((http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization.ToString(),
                    http.Request.Headers["X-TerraScale-Region-Pin"].ToString()));
                if (shapeMode == "unavailable") return Task.FromResult<IResult?>(Results.StatusCode(503));
                if (shapeMode == "malformed") return Task.FromResult<IResult?>(Results.Text("{broken", "application/json"));
                Dictionary<string, object?> Shape(string collection, long queries, double total, double maximum, double? p95, DateTimeOffset lastSeen) => new()
                {
                    ["id"] = new string('a', 64),
                    ["collection"] = collection,
                    ["shape"] = longShape,
                    ["queries"] = queries,
                    ["totalLatencyMs"] = total,
                    ["maxLatencyMs"] = maximum,
                    ["rowsScanned"] = 21,
                    ["lastSeenAt"] = lastSeen,
                    ["p95LatencyMs"] = p95,
                    ["latencyBuckets"] = p95 is null ? null : new long[] { 0, 1, 1 }
                };
                var legacy = Shape("legacy", 2, 11, 300, null, seen);
                legacy.Remove("p95LatencyMs"); legacy.Remove("latencyBuckets");
                var shapeItems = new[]
                {
                    Shape("popular", 100, 1234, 900, 100, seen), legacy,
                    Shape("slowest", 2, 25, 800, 400, seen),
                    Shape("null-p95", 0, 0, 200, null, seen),
                    Shape("stale", 1, 9999, 9999, 9999, seen.AddHours(-25)),
                    Shape("future", 1, 9999, 9999, 9999, seen.AddHours(1))
                };
                return Task.FromResult<IResult?>(Results.Json(new { items = shapeMode == "empty" ? [] : shapeItems }));
            }
            if (databaseId.Length == 0 || !http.Request.Path.Value!.EndsWith("/logs", StringComparison.Ordinal))
                return Task.FromResult<IResult?>(null);
            requests.Enqueue(http.Request.Path + http.Request.QueryString);
            if (mode == "unavailable") return Task.FromResult<IResult?>(Results.StatusCode(503));
            var now = DateTimeOffset.UtcNow;
            var route = "/v1/projects/{project}/databases/{database}/";
            object Entry(string id, int back, string method, string path, int status, double latency, bool query) => new
            { id, requestId = id, time = now.AddSeconds(-back), region = "gru", method, route = route + path, status, latencyMs = latency, operations = 1, isQuery = query };
            var entries = new[]
            {
                Entry("01JA0000000000000000000013", 1, "POST", "sql", 500, 100, true),
                Entry("01JA0000000000000000000012", 2, "POST", "collections/{collection}/query", 200, 99.99, true),
                Entry("01JA0000000000000000000011", 3, "GET", "collections", 200, 800, false)
            };
            var status = http.Request.Query["status"].ToString();
            var items = mode == "empty" ? Array.Empty<object>() : status == "5xx" ? entries.Take(1).ToArray() : entries;
            return Task.FromResult<IResult?>(Results.Json(new
            {
                databaseId = mode == "foreign" ? "01JA0000000000000000000099" : databaseId,
                items,
                truncated = true,
                recordingLimited = mode == "limited"
            }));
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "logged-app", ["region"] = "GRU" }))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            databaseId = body.RootElement.GetProperty("id").GetString()!;
        }
        var path = "/databases/" + databaseId + "/logs";
        var all = await PageAsync(path, HttpStatusCode.OK);
        await Assert.That(all).Contains("Request logs");
        await Assert.That(all).Contains("500</span>");
        await Assert.That(all).Contains("100 ms");
        await Assert.That(all).Contains("aria-current=\"page\"><span>Logs</span>");
        await Assert.That(all).Contains("Only the latest 100 recorded requests are searched by this filter.");
        var slow = all[all.IndexOf("id=\"slow-queries-title\"", StringComparison.Ordinal)..];
        await Assert.That(slow).Contains("Run the query with \"explain\": true to see its plan and a suggested index.");
        await Assert.That(slow).Contains("title=\"" + longShape + "\"");
        await Assert.That(slow).Contains(longShape[..100] + "…</code>");
        await Assert.That(slow).Contains("<td title=\"2\">2</td><td>Not reported</td><td>5.50 ms</td><td>10.5</td>");
        await Assert.That(slow).Contains("<td title=\"0\">0</td><td>Not reported</td><td>Not reported</td><td>—</td>");
        await Assert.That(slow.IndexOf("slowest</td>", StringComparison.Ordinal) < slow.IndexOf("legacy</td>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(slow.IndexOf("legacy</td>", StringComparison.Ordinal) < slow.IndexOf("null-p95</td>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(slow.IndexOf("null-p95</td>", StringComparison.Ordinal) < slow.IndexOf("popular</td>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(slow).DoesNotContain("stale</td>");
        await Assert.That(slow).DoesNotContain("future</td>");
        var shapeRequest = shapeRequests.Last();
        await Assert.That(shapeRequest.Path).EndsWith("/databases/logged-app/query-shapes");
        await Assert.That(shapeRequest.Token).StartsWith("Bearer ");
        await Assert.That(shapeRequest.Pin).IsEqualTo("gru");
        using (var encoded = await client.GetAsync(path))
            await Assert.That(await encoded.Content.ReadAsStringAsync()).DoesNotContain("<script>alert(1)</script>");
        var filtered = await PageAsync(path + "?status=5xx", HttpStatusCode.OK);
        await Assert.That(filtered).Contains("500</span>");
        await Assert.That(filtered).DoesNotContain("200</span>");
        await Assert.That(filtered).Contains("slowest</td>");
        await Assert.That(requests.Last()).EndsWith("/databases/logged-app/logs?status=5xx");

        mode = "limited";
        await Assert.That(await PageAsync(path, HttpStatusCode.OK)).Contains("Some requests may be missing.");
        await Assert.That(all).Contains("Operations counts committed document operations, not billing units.");
        await Assert.That(all).Contains("stored records are pruned on the next database log flush.");
        mode = "empty";
        await Assert.That(await PageAsync(path, HttpStatusCode.OK)).Contains("No recorded requests match this filter");
        foreach (var failed in new[] { "unavailable", "foreign" })
        {
            mode = failed;
            var unavailable = await PageAsync(path, HttpStatusCode.ServiceUnavailable);
            await Assert.That(unavailable).Contains("Request logs are unavailable.");
            await Assert.That(unavailable).DoesNotContain("No recorded requests match this filter");
            await Assert.That(unavailable).Contains("slowest</td>");
        }
        mode = "records";
        shapeMode = "empty";
        await Assert.That(await PageAsync(path, HttpStatusCode.OK)).Contains("No query shapes seen in the last 24 hours.");
        foreach (var failed in new[] { "unavailable", "malformed" })
        {
            shapeMode = failed;
            var unavailable = await PageAsync(path, HttpStatusCode.OK);
            await Assert.That(unavailable).Contains("Slow queries are unavailable.");
            await Assert.That(unavailable).Contains("500</span>");
            await Assert.That(unavailable).DoesNotContain("No query shapes seen");
        }
        var count = requests.Count;
        var shapeCount = shapeRequests.Count;
        await Assert.That(await PageAsync(path + "?status=invalid", HttpStatusCode.BadRequest)).Contains("Choose a valid status filter.");
        await Assert.That(requests.Count).IsEqualTo(count);
        await Assert.That(shapeRequests.Count).IsEqualTo(shapeCount);

        async Task<string> PageAsync(string url, HttpStatusCode expected)
        {
            using var response = await client.GetAsync(url);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        }
    }

    [Test]
    public async Task Producer_contract_rejects_missing_identity_duplicate_ids_and_invalid_measurements()
    {
        const string database = "01JA0000000000000000000004";
        var row = new DatabaseLogEntry("01JA0000000000000000000013", DateTimeOffset.UtcNow, "gru", "GET",
            "/v1/projects/{project}/databases/{database}/collections", 200, 1, 1, false, "01JA0000000000000000000014");
        foreach (var batch in new[]
        {
            new DatabaseLogBatch("01JA0000000000000000000005", [row], false, false),
            new DatabaseLogBatch(database, [row, row], false, false),
            new DatabaseLogBatch(database, [row with { Id = "not-a-ulid" }], false, false),
            new DatabaseLogBatch(database, [row with { RequestId = "private?token=value" }], false, false),
            new DatabaseLogBatch(database, [row with { LatencyMs = double.NaN }], false, false),
            new DatabaseLogBatch(database, [row with { Operations = -1 }], false, false),
            new DatabaseLogBatch(database, [row with { Route = "/route?private=value" }], false, false)
        })
        {
            var rejected = false;
            try { DatabaseLogBackend.Validate(batch, database, ""); }
            catch (JsonException) { rejected = true; }
            await Assert.That(rejected).IsTrue();
        }
    }
}
