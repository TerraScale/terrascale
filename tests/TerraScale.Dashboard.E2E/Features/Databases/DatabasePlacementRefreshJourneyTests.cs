using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabasePlacementRefreshJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task CanonicalPlacementRefreshesDetailAndMcpButCannotOverwriteANewerDashboardSave()
    {
        var id = "";
        var projectId = "";
        var mode = "normal";
        var pausedListings = 0;
        var expansionArmed = false;
        var expansionReads = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (Volatile.Read(ref mode) == "expansion-outage")
            {
                if (http.Request.Method == "PATCH") Volatile.Write(ref expansionArmed, true);
                if (Volatile.Read(ref expansionArmed) && (http.Request.Path == "/v1/regions" || http.Request.Path == "/healthz") && Interlocked.Increment(ref expansionReads) > 1)
                    return Results.Json(new { error = new { code = "unavailable", message = "Test discovery outage after placement validation" } }, statusCode: 503);
            }
            if (http.Request.Method != "GET" || http.Request.Path != "/v1/projects/" + projectId + "/databases") return null;
            var answer = Volatile.Read(ref mode);
            if (answer is "normal" or "expansion-outage") return null;
            if (answer.StartsWith("paused", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref pausedListings) != 1) return null;
                entered.TrySetResult(); await release.Task.WaitAsync(http.RequestAborted);
            }
            http.Response.Headers["X-TerraBase-Region"] = "gru";
            var items = new[] { new { id, projectId, name = "refresh", branchedFrom = (string?)null, createdAt = DateTimeOffset.UnixEpoch,
                region = "gru", homeRegion = "gru", replicaRegions = answer == "invalid" ? new[] { "fra", "fra" } : new[] { "fra", "sfo" }, allRegions = false } };
            return Results.Json(new { items = answer == "paused-missing" ? [] : items, next = (string?)null });
        });
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        var organizationId = identity.GetProperty("organizationId").GetString()!;
        projectId = identity.GetProperty("projectId").GetString()!;
        await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "sfo", "fra" } });
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "refresh", ["region"] = "GRU" }))
        { await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created); id = (await JsonAsync(created)).GetProperty("id").GetString()!; }
        var secret = await HttpJourney.CreatePatAsync(client, "Placement refresh", account.Password);
        // A direct detail GET must discover an external placement change without first visiting the list.
        await producer.PostAsJsonAsync("/__test/control", new { command = "add-replica", value = "fra" });
        var detail = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + id + "/replication"));
        await Assert.That(detail).Contains("<dt>Configured regions</dt><dd>gru · São Paulo, sfo · San Francisco, fra · Frankfurt</dd>");
        using (var narrowed = await SaveAsync(false)) await Assert.That(narrowed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await producer.PostAsJsonAsync("/__test/control", new { command = "add-replica", value = "sfo" });
        var rpc = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_database", arguments = new { databaseId = id, organizationId, projectId } })).GetProperty("result");
        await Assert.That(rpc.GetProperty("structuredContent").GetProperty("placement").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "FRA", "SFO" });
        // Invalid canonical placement must leave the last validated record intact.
        Volatile.Write(ref mode, "invalid");
        var listed = await ReadAsync(client, "/api/v1/dashboard/databases");
        await Assert.That(listed[0].GetProperty("regions").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "FRA", "SFO" });
        foreach (var race in new[] { "paused", "paused-missing", "paused-all" })
        {
            Volatile.Write(ref mode, "normal");
            using (var reset = await SaveAsync(false)) await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await producer.PostAsJsonAsync("/__test/control", new { command = "add-replica", value = "sfo" });
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); pausedListings = 0;
            Volatile.Write(ref mode, race);
            var pending = client.GetAsync("/api/v1/dashboard/databases");
            var allRegions = race == "paused-all";
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                using var saved = await SaveAsync(allRegions);
                await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }
            finally { release.TrySetResult(); }
            var expected = allRegions ? new[] { "GRU" } : new[] { "GRU", "FRA" };
            using (var completed = await pending)
            {
                var refreshed = (await JsonAsync(completed))[0];
                await Assert.That(refreshed.TryGetProperty("placementRevision", out _)).IsFalse();
                await Assert.That(refreshed.GetProperty("allRegions").GetBoolean()).IsEqualTo(allRegions);
                await Assert.That(refreshed.GetProperty("regions").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(expected)
                    .Because("a stale listing must not replace or delete a newer save, even when the saved values repeat the snapshot");
            }
            var context = await ReadAsync(client, "/api/v1/dashboard/context");
            var stored = context.GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == organizationId)
                .GetProperty("projects").GetProperty(projectId).GetProperty("databases").GetProperty(id);
            await Assert.That(stored.GetProperty("regions").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(expected);
        }
        Volatile.Write(ref mode, "normal");
        var all = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_database", arguments = new { databaseId = id, organizationId, projectId } })).GetProperty("result").GetProperty("structuredContent");
        await Assert.That(all.GetProperty("placement").GetArrayLength()).IsEqualTo(3);
        await Assert.That(all.GetProperty("regions").GetArrayLength()).IsEqualTo(3);
        var list = await client.GetStringAsync("/databases");
        await Assert.That(list).Contains("<span class=\"ts-badge ts-badge-info\">All regions</span>");
        await Assert.That(list).Contains("<dt class=\"ts-stat-label\"><span class=\"ts-dot\" aria-hidden=\"true\"></span>Regions</dt><dd class=\"ts-stat-value\">3</dd>");
        // Discovery fails only after the mutation's canonical placement was validated and committed.
        Volatile.Write(ref mode, "expansion-outage");
        var changed = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "set_database_regions", arguments = new { databaseId = id, allRegions = true, organizationId, projectId } })).GetProperty("result");
        await Assert.That(changed.GetProperty("structuredContent").GetProperty("allRegions").GetBoolean()).IsTrue();
        var fallback = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "list_databases", arguments = new { organizationId, projectId } })).GetProperty("result").GetProperty("structuredContent").GetProperty("databases")[0];
        await Assert.That(fallback.GetProperty("allRegions").GetBoolean()).IsTrue();
        await Assert.That(fallback.GetProperty("placement").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU" });

        Task<HttpResponseMessage> SaveAsync(bool all)
        {
            var fields = new List<KeyValuePair<string, string>> { new("_csrf", csrf), new("databaseId", id), new("region", "GRU"), new("allRegions", all ? "true" : "false") };
            if (!all) fields.Add(new("region", "FRA"));
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/databases/regions") { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            return client.SendAsync(request);
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return await JsonAsync(response); }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
}
