using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class FunctionJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task Functions_page_is_empty_without_the_api_and_shows_source_logs_deliveries_and_deletes()
    {
        var databaseId = "";
        var mode = "missing";
        var requests = new ConcurrentQueue<string>();
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            var path = http.Request.Path.Value!;
            var at = path.IndexOf("/functions", StringComparison.Ordinal);
            if (databaseId.Length == 0 || at < 0) return Task.FromResult<IResult?>(null);
            requests.Enqueue(http.Request.Method + " " + path[at..] + http.Request.QueryString +
                (http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ? "" : " anonymous"));
            if (mode == "missing") return Task.FromResult<IResult?>(Results.NotFound());
            if (mode == "unavailable") return Task.FromResult<IResult?>(Results.StatusCode(503));
            var now = DateTimeOffset.UtcNow;
            object Function(string name, object? trigger, string? cron, bool source) => new
            { name, source = source ? "function handler(event) { return { status: 200, body: \"<b>hi</b>\" }; }" : null, trigger, cron, createdAt = now.AddDays(-1), updatedAt = now };
            var hello = Function("hello", null, null, true);
            var orders = Function("on-order", new { collection = "orders", events = new[] { "upsert" } }, null, true);
            IResult result = path[at..] switch
            {
                "/functions" => Results.Json(new[] { Function("hello", null, null, false), Function("on-order", new { collection = "orders", events = new[] { "upsert" } }, null, false), Function("nightly", null, "0 3 * * *", false) }),
                "/functions/hello" when http.Request.Method == "DELETE" => Results.StatusCode(204),
                "/functions/hello" => Results.Json(hello),
                "/functions/on-order" => Results.Json(orders),
                "/functions/hello/logs" or "/functions/on-order/logs" => Results.Json(new object[]
                {
                    new { id = "01JA0000000000000000000022", at = now, trigger = "change", status = "error", durationMs = 12.5, lines = new[] { "saw order 42" }, error = "TypeError: boom" },
                    new { id = "01JA0000000000000000000021", at = now.AddSeconds(-5), trigger = "http", status = "ok", durationMs = 3, lines = Array.Empty<string>(), error = (string?)null }
                }),
                "/functions/on-order/deliveries" => Results.Json(new[] { new { id = "01JA0000000000000000000031", createdAt = now, status = "pending", attempts = 3, lastAttemptAt = now, nextAttemptAt = now.AddMinutes(4), httpStatus = (int?)null, errorCode = "target_rejected" } }),
                _ => Results.NotFound()
            };
            return Task.FromResult<IResult?>(result);
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "fn-app", ["region"] = "GRU" }))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            databaseId = body.RootElement.GetProperty("id").GetString()!;
        }
        var path = "/databases/" + databaseId + "/functions";

        // Before TerraBase serves functions, the page renders its empty state.
        var empty = await PageAsync(path, HttpStatusCode.OK);
        await Assert.That(empty).Contains("aria-current=\"page\"><span>Functions</span>");
        await Assert.That(empty).Contains("No functions yet.");
        await Assert.That(empty).DoesNotContain("ts-alert-danger");
        await Assert.That(requests.Single()).IsEqualTo("GET /functions");

        mode = "live";
        var list = await PageAsync(path, HttpStatusCode.OK);
        await Assert.That(list).Contains("Changes to orders (upsert)");
        await Assert.That(list).Contains("Cron 0 3 * * * UTC");
        await Assert.That(list).Contains("HTTPS calls");
        var detail = await PageAsync(path + "?function=on-order", HttpStatusCode.OK);
        await Assert.That(detail).Contains("POST https://api.terrascale.tech/v1/projects/");
        await Assert.That(detail).Contains("/databases/fn-app/functions/on-order/invoke");
        await Assert.That(detail).Contains("function handler(event)");
        await Assert.That(detail).Contains("id=\"function-logs\"");
        await Assert.That(detail).Contains("saw order 42");
        await Assert.That(detail).Contains("TypeError: boom");
        await Assert.That(detail).Contains("01JA0000000000000000000031");
        await Assert.That(detail).Contains("target_rejected");
        await Assert.That(requests).Contains("GET /functions/on-order/logs?limit=25");
        await Assert.That(requests.Any(item => item.EndsWith("anonymous", StringComparison.Ordinal))).IsFalse();
        var http = await PageAsync(path + "?function=hello", HttpStatusCode.OK);
        await Assert.That(http).DoesNotContain("Trigger deliveries");
        await Assert.That(http).Contains("Exchange your project API key for a short-lived access token with <code>documents:read</code>, then send that access token as a Bearer token.");
        var guide = Regex.Match(http, "<a\\b[^>]*href=\"https://docs.terrascale.tech/guides/authentication\"[^>]*>.*?</a>", RegexOptions.Singleline).Value;
        await Assert.That(guide).Contains("Authentication guide");
        await Assert.That(guide).Contains("target=\"_blank\"");
        await Assert.That(guide).Contains("rel=\"noopener noreferrer\"");
        await Assert.That(guide).Contains("opens in a new tab");
        await Assert.That(requests).DoesNotContain("GET /functions/hello/deliveries");
        await Assert.That(await PageAsync(path + "?function=gone", HttpStatusCode.NotFound)).Contains("This function does not exist.");
        await Assert.That(await PageAsync(path + "?function=Bad%20Name", HttpStatusCode.NotFound)).Contains("This function does not exist.");

        mode = "unavailable";
        var unavailable = await PageAsync(path, HttpStatusCode.ServiceUnavailable);
        await Assert.That(unavailable).Contains("Functions are unavailable.");

        mode = "live";
        var form = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(client, path), ["databaseId"] = databaseId, ["name"] = "hello" };
        var noCsrf = new Dictionary<string, string>(form); noCsrf.Remove("_csrf");
        using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/functions/delete", noCsrf))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(requests).DoesNotContain("DELETE /functions/hello");
        using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/functions/delete", form))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(deleted.Headers.Location!.ToString()).IsEqualTo(path);
        }
        await Assert.That(requests).Contains("DELETE /functions/hello");
        using (var controls = scenario.CreateProviderClient())
        using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "viewer" })) role.EnsureSuccessStatusCode();
        using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/functions/delete", form))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        async Task<string> PageAsync(string url, HttpStatusCode expected)
        {
            using var response = await client.GetAsync(url);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        }
    }
}
