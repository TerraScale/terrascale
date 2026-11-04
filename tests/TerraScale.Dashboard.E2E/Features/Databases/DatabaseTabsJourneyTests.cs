using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseTabsJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task DatabasePagesOfferNoWebhooksTabAndEveryListedTabAnswersItself()
        {
            var databaseId = "";
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                var path = http.Request.Path.Value!;
                return Task.FromResult<IResult?>(path.IndexOf("/functions", StringComparison.Ordinal) >= 0
                    ? Results.Json(Array.Empty<object>())
                    : path.EndsWith("/logs", StringComparison.Ordinal)
                        ? Results.Json(new { databaseId, items = Array.Empty<object>(), truncated = false, recordingLimited = false })
                        : path.EndsWith("/query-shapes", StringComparison.Ordinal)
                            ? Results.Json(new { items = Array.Empty<object>() })
                            : null);
            });
            using var client = scenario.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "tab-journey", ["homeRegion"] = "GRU" }))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                databaseId = body.RootElement.GetProperty("id").GetString()!;
            }

            using var overview = await client.GetAsync("/databases/" + databaseId);
            await Assert.That(overview.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = WebUtility.HtmlDecode(await overview.Content.ReadAsStringAsync());
            var tabs = Regex.Matches(html, "<nav class=\"ts-tabs ts-db-tabs\"[^>]*>.*?</nav>", RegexOptions.Singleline).Single().Value;
            var labels = Regex.Matches(tabs, "<span>([^<]+)</span>").Select(match => match.Groups[1].Value).ToArray();
            await Assert.That(labels).IsEquivalentTo(new[] { "Overview", "Documents", "SQL", "Schema", "Indexes", "Functions",
                "Replication", "Logs", "Backups", "Branches", "Import and export", "Settings", "Danger zone" });
            await Assert.That(html.Contains("Webhooks", StringComparison.Ordinal)).IsFalse()
                .Because("TerraBase removed its collection change webhook backend; the dashboard must not offer the dead tab");
            await Assert.That(html.Contains("/webhooks", StringComparison.Ordinal)).IsFalse();

            foreach (var key in new[] { "", "/documents", "/sql", "/schema", "/indexes", "/functions", "/replication", "/logs",
                "/backups", "/branches", "/import", "/settings", "/danger" })
            {
                using var page = await client.GetAsync("/databases/" + databaseId + key);
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("tab " + key);
            }

            using (var removed = await client.GetAsync("/databases/" + databaseId + "/webhooks"))
            {
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "tab-journey" });
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
    }
}
