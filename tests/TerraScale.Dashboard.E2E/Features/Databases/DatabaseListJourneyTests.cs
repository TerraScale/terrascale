using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseListJourneyTests(DashboardRootFixture fixture)
    {
        private static readonly string[] Catalog = ["gru", "fra", "sjc"];

        [Test]
        public async Task DatabasesPageRendersWhenAnalyticsRepeatsACounter()
        {
            string? databaseId = null;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http => Task.FromResult(RepeatedCounter(http, databaseId)), replica: false);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            using (var catalog = await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = Catalog }))
            {
                await Assert.That(catalog.IsSuccessStatusCode).IsTrue();
            }

            await DatabaseJourney.NativeWorkspaceAsync(client);
            databaseId = await HttpJourney.CreateDatabaseAsync(client, "orders");
            var me = JsonElement.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var scope = "organizationId=" + me.GetProperty("organizationId").GetString() + "&projectId=" + me.GetProperty("projectId").GetString();

            using (var list = await client.GetAsync("/databases?" + scope))
            {
                var html = WebUtility.HtmlDecode(await list.Content.ReadAsStringAsync());
                await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(html).Contains(DatabaseService.Path(databaseId));
                await Assert.That(html).Contains("Could not load database metrics. Reload the page to retry.");
            }

            using var overview = await client.GetAsync(DatabaseService.Path(databaseId) + "?" + scope);
            await Assert.That(overview.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(WebUtility.HtmlDecode(await overview.Content.ReadAsStringAsync())).Contains("Could not load activity measurements. Reload the page to retry.");
        }

        private static IResult? RepeatedCounter(HttpContext http, string? databaseId)
        {
            var path = http.Request.Path.Value ?? "";
            if (databaseId is null || http.Request.Method != "GET" || !path.StartsWith("/v1/projects/", StringComparison.Ordinal) || !path.EndsWith("/analytics", StringComparison.Ordinal))
            {
                return null;
            }

            var bucket = http.Request.Query["range"].ToString() switch { "1h" => 60, "24h" => 3600, "7d" => 21600, _ => 86400 };
            var to = DateTimeOffset.FromUnixTimeSeconds((DateTimeOffset.UtcNow.ToUnixTimeSeconds() / bucket + 1) * bucket);
            var from = to.AddSeconds(-bucket * (bucket switch { 60 => 60, 3600 => 24, 21600 => 28, _ => 30 }));
            var start = to.AddSeconds(-bucket);
            var body = JsonSerializer.Serialize(new
            {
                projectId = path.Split('/')[3],
                range = http.Request.Query["range"].ToString(),
                from,
                to,
                bucketSeconds = bucket,
                items = new[]
                {
                    new
                    {
                        databaseId,
                        databaseName = "orders",
                        region = "gru",
                        series = new[]
                        {
                            new
                            {
                                start,
                                requests = new Dictionary<string, long> { ["GET"] = 2 },
                                operations = new Dictionary<string, long> { ["read"] = 1 },
                                errors = 0,
                                latency = new { p50Ms = 1, p95Ms = 2, p99Ms = 3 },
                                storageBytes = 4096,
                                clientBandwidth = new { bytesSent = 10, bytesReceived = 5 },
                                statements = new Dictionary<string, long>()
                            }
                        }
                    }
                }
            });
            return Results.Text(body.Replace("\"operations\":{\"read\":1}", "\"operations\":{\"read\":1,\"read\":1}", StringComparison.Ordinal), "application/json");
        }
    }
}
