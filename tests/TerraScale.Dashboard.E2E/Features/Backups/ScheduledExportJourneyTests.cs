using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Backups
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class ScheduledExportJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task OwnersSaveTheirBucketScheduleExportsAndHearOfAFailedRunOnce()
        {
            var failed = 0;
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
                Task.FromResult<IResult?>(Volatile.Read(ref failed) == 1 && http.Request.Method == "GET" && http.Request.Path.Value!.EndsWith("/export-schedule", StringComparison.Ordinal)
                    ? Results.Json(new
                    {
                        cron = "0 3 * * *",
                        updatedAt = DateTimeOffset.UtcNow,
                        lastRun = new
                        {
                            startedAt = DateTimeOffset.Parse("2026-10-24T03:00:00Z"),
                            finishedAt = DateTimeOffset.Parse("2026-10-24T03:00:09Z"),
                            status = "failed",
                            key = (string?)null,
                            documents = 0L,
                            bytes = 0L,
                            error = "bucket refused the write"
                        }
                    }) : null));
            using var client = host.CreateClient();
            using var producer = host.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "exported", ["region"] = "GRU" });
            var databaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            var page = "/databases/" + databaseId + "/backups";
            await Assert.That(await client.GetStringAsync(page)).Contains("No bucket saved for this project.");
            using (var early = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/export-schedule", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["cron"] = "0 3 * * *" }))
            {
                await Assert.That(early.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

            using (var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/destination", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["endpoint"] = "https://fly.storage.tigris.dev", ["region"] = "auto", ["bucket"] = "customer-exports", ["accessKeyId"] = "tid_synthetic", ["secretKey"] = "tsec_synthetic_secret" }))
            {
                await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            using (var weekly = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/export-schedule", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["cron"] = "0 3 * * 0" }))
            {
                await Assert.That(JsonDocument.Parse(await weekly.Content.ReadAsStringAsync()).RootElement.GetProperty("cron").GetString()).IsEqualTo("0 3 * * 0");
            }

            using (var invalid = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/export-schedule", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["cron"] = "99 3 * * *" }))
            {
                await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            var html = WebUtility.HtmlDecode(await client.GetStringAsync(page));
            await Assert.That(html).Contains("customer-exports").And.Contains("value=\"0 3 * * 0\" selected=\"selected\"").And.Contains("No scheduled export has run yet.");
            await Assert.That(html).DoesNotContain("tsec_synthetic_secret").And.DoesNotContain("tid_synthetic");
            var removal = System.Text.RegularExpressions.Regex.Match(html, "<dialog id=\"remove-backup-bucket\"[\\s\\S]*?</dialog>").Value;
            await Assert.That(removal).Contains("Remove bucket: customer-exports").And.Contains("data-submit-once").And.Contains("deletes its saved credentials");

            Volatile.Write(ref failed, 1);
            await Assert.That(WebUtility.HtmlDecode(await client.GetStringAsync(page))).Contains("The last scheduled export failed").And.Contains("bucket refused the write");
            await client.GetStringAsync(page);
            var inbox = WebUtility.HtmlDecode(await client.GetStringAsync("/notifications"));
            await Assert.That(inbox.Split("exported: bucket refused the write").Length - 1).IsEqualTo(1).Because("one failed run notifies the owner once");
            using (var removed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/destination-remove", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId }))
            {
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            using var observed = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
            await Assert.That(observed.RootElement.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }
    }
}
