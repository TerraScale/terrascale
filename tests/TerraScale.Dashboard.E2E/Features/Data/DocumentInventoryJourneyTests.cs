using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DocumentInventoryJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("rows")]
        [Arguments("counts-failure")]
        [Arguments("listing-failure")]
        [Arguments("empty")]
        public async Task IndependentInventoriesOverlapAndUnusedCountsDoNotBreakThePage(string outcome)
        {
            var listingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var countsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCounts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var armed = false;
            var browseReads = 0;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                if (!Volatile.Read(ref armed))
                {
                    return null;
                }

                var path = http.Request.Path.Value!;
                if (path.EndsWith("/query", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref browseReads);
                }

                if (http.Request.Method != "GET")
                {
                    return null;
                }

                if (path.EndsWith("/collections", StringComparison.Ordinal))
                {
                    listingStarted.TrySetResult();
                    await countsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), http.RequestAborted);
                    if (outcome == "listing-failure")
                    {
                        return Results.Json(new { error = new { code = "unavailable", message = "Collection listing unavailable." } }, statusCode: 503);
                    }

                    if (outcome == "empty")
                    {
                        return Results.Json(new { items = Array.Empty<object>(), next_cursor = (string?)null });
                    }
                }
                if (path.EndsWith("/schema", StringComparison.Ordinal))
                {
                    countsStarted.TrySetResult();
                    await listingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), http.RequestAborted);
                    await releaseCounts.Task.WaitAsync(TimeSpan.FromSeconds(10), http.RequestAborted);
                    if (outcome != "rows")
                    {
                        return Results.Json(new { error = new { code = "unavailable", message = "Counts unavailable." } }, statusCode: 503);
                    }
                }
                return null;
            });
            using var client = scenario.CreateClient();
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "inventory", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = database.RootElement.GetProperty("id").GetString()!;
            using var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "items" });
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
            Volatile.Write(ref armed, true);
            var page = client.GetAsync($"/databases/{id}/documents");
            try
            {
                await Task.WhenAll(listingStarted.Task, countsStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
                // Unused counts may finish after the response, including with a failure.
                if (outcome is "listing-failure" or "empty")
                {
                    await page.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally { releaseCounts.TrySetResult(); }
            using var response = await page;
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(html).DoesNotContain("Counts unavailable.");
            if (outcome is "listing-failure" or "empty")
            {
                await Assert.That(browseReads).IsEqualTo(0);
                if (outcome == "listing-failure")
                {
                    await Assert.That(html).Contains("Document results unavailable.").And.Contains("Retry documents</a>")
                        .And.Contains("The data plane could not complete the request. Try again shortly.").And.DoesNotContain("No collections yet.");
                }
                else
                {
                    await Assert.That(html).Contains("No collections yet.").And.DoesNotContain("Retry documents</a>");
                }
            }
            else
            {
                await Assert.That(browseReads).IsEqualTo(1);
                await Assert.That(html).Contains("data-collection=\"items\"");
                await Assert.That(html.Contains("items (0)", StringComparison.Ordinal)).IsEqualTo(outcome == "rows");
            }
        }
    }
}
