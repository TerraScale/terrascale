using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Backups;

namespace TerraScale.Dashboard.E2E.Features.Backups;

[ClassDataSource<DashboardRootFixture>]
public sealed class SelectiveRecoveryJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task PreviewAndExplicitSubsetApprovalUseTheOriginalDatabaseAndClearStaleApprovals()
    {
        const string first = "01K78D0C000000000000000011", second = "01K78D0C000000000000000012";
        var calls = new List<(string Path, JsonElement Body)>();
        var stale = true;
        var unavailable = false;
        var noHistory = true;
        var earliest = DateTimeOffset.Parse("2026-10-25T12:00:00.000Z", CultureInfo.InvariantCulture);
        var latest = earliest.AddMinutes(10);
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.Contains("/point-in-time", StringComparison.Ordinal)) return null;
            await Assert.That(http.Request.Host.Host).IsEqualTo("gru.api.terrascale.tech");
            await Assert.That(http.Request.Headers["X-TerraScale-Region-Pin"].ToString()).IsEqualTo("gru");
            await Assert.That(http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)).IsTrue();
            if (http.Request.Method == "GET") return noHistory ? Results.NotFound() : Results.Json(new { earliest, latest });
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            calls.Add((http.Request.Path.Value, body.RootElement.Clone()));
            if (unavailable) { http.Response.Headers.RetryAfter = "7"; return Results.StatusCode(503); }
            if (http.Request.Path.Value.EndsWith("/recovery-preview", StringComparison.Ordinal))
                return Results.Json(new
                {
                    approval = "test-only-approval",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    records = new[]
                    {
                        new { collection = "users", id = first, historical = new { score = 1, padding = new string('x', 70000) }, current = (object?)new { score = 2 }, currentVersion = "live-etag" },
                        new { collection = "users", id = second, historical = new { score = 3, padding = "" }, current = (object?)null, currentVersion = "tombstone-etag" }
                    }
                });
            return stale ? Results.StatusCode(409) : Results.Json(new { offset = "42", durability = "local_durable", results = new[] { new { id = second, version = "restored-etag" } } });
        });
        using var client = host.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "recovery-source", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = database.RootElement.GetProperty("id").GetString()!;
        var path = "/databases/" + id + "/recovery";
        using (var empty = await client.GetAsync(path))
            await Assert.That(await empty.Content.ReadAsStringAsync()).Contains("No retained PITR history");
        using var listing = await client.GetFromJsonAsync<JsonDocument>("/api/v1/dashboard/backups?databaseId=" + id);
        await Assert.That(listing!.RootElement.GetProperty("backups").GetArrayLength()).IsEqualTo(0);
        noHistory = false;
        using (var available = await client.GetAsync(path))
            await Assert.That(await available.Content.ReadAsStringAsync()).Contains("value=\"" + RecoveryWindow.Input(latest) + "\"")
                .And.Contains("min=\"" + RecoveryWindow.Input(earliest) + "\"").And.Contains("max=\"" + RecoveryWindow.Input(latest) + "\"");
        var selectedPoint = earliest.AddMinutes(5);
        var point = RecoveryWindow.Input(selectedPoint);
        var preview = new Dictionary<string, string> { ["_csrf"] = csrf, ["operation"] = "preview", ["point"] = point, ["collection"] = "users", ["ids"] = "" };
        foreach (var outside in new[] { earliest.AddMilliseconds(-1), latest.AddMilliseconds(1) })
        {
            preview["point"] = RecoveryWindow.Input(outside);
            using var refused = await HttpJourney.PostFormAsync(client, path, preview);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        }
        await Assert.That(calls.Count).IsEqualTo(0);
        preview["point"] = point;
        await PreviewAsync();
        await Assert.That(calls[0].Path).EndsWith("/databases/recovery-source/point-in-time/recovery-preview");
        var selection = calls[0].Body.GetProperty("selections")[0];
        await Assert.That(selection.GetProperty("collection").GetString()).IsEqualTo("users");
        await Assert.That(selection.TryGetProperty("ids", out _)).IsFalse();
        await Assert.That(calls[0].Body.GetProperty("pointInTime").GetDateTimeOffset()).IsEqualTo(selectedPoint);
        var apply = new Dictionary<string, string> { ["_csrf"] = csrf, ["operation"] = "apply", ["collection"] = "users", ["approval"] = "test-only-approval" };
        using (var none = await HttpJourney.PostFormAsync(client, path, apply))
            await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(calls.Count).IsEqualTo(1);
        apply["approved"] = second;
        using (var conflict = await HttpJourney.PostFormAsync(client, path, apply))
        {
            await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await conflict.Content.ReadAsStringAsync()).Contains("No records were restored").And.DoesNotContain("name=\"approval\"");
        }
        await PreviewAsync();
        stale = false;
        using (var restored = await HttpJourney.PostFormAsync(client, path, apply))
        {
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await restored.Content.ReadAsStringAsync()).Contains("Restored 1 approved record(s) into recovery-source").And.DoesNotContain("name=\"approval\"");
        }
        var applied = calls[^1].Body;
        await Assert.That(applied.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(new[] { "approval", "records" });
        await Assert.That(applied.GetProperty("records").GetArrayLength()).IsEqualTo(1);
        await Assert.That(applied.GetProperty("records")[0].GetProperty("id").GetString()).IsEqualTo(second);
        await Assert.That(applied.GetProperty("records")[0].EnumerateObject().Select(property => property.Name)).IsEquivalentTo(new[] { "collection", "id" });
        preview["ids"] = first + "\n" + second;
        await PreviewAsync();
        await Assert.That(calls[^1].Body.GetProperty("selections")[0].GetProperty("ids").GetArrayLength()).IsEqualTo(2);
        unavailable = true;
        using (var capacity = await HttpJourney.PostFormAsync(client, path, preview))
        {
            await Assert.That(capacity.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await capacity.Content.ReadAsStringAsync()).Contains("Retry after 7 seconds").And.DoesNotContain("name=\"approval\"");
        }
        preview["_csrf"] = "invalid";
        using (var refused = await HttpJourney.PostFormAsync(client, path, preview))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using (var foreign = await client.GetAsync("/databases/" + AccountService.NewId() + "/recovery"))
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var provider = host.CreateProviderClient();
        using var role = await provider.PostAsJsonAsync("/__test/control", new { command = "role", value = "member" });
        using (var forbidden = await client.GetAsync(path))
            await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(calls.Count).IsEqualTo(6);

        async Task PreviewAsync()
        {
            using var response = await HttpJourney.PostFormAsync(client, path, preview);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("Retained value to restore").And.Contains("live-etag").And.Contains("tombstone-etag").And.Contains("Currently absent");
            var approvals = Regex.Matches(html, "<input[^>]+name=\"approved\"[^>]*>");
            await Assert.That(approvals.Count).IsEqualTo(2);
            await Assert.That(approvals.All(input => !input.Value.Contains(" checked", StringComparison.Ordinal))).IsTrue();
        }
    }
}
