using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class FunctionJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task Functions_ui_and_mcp_share_source_logs_deliveries_deletion_and_project_authority()
        {
            var databaseId = "";
            var mode = "missing";
            var requests = new ConcurrentQueue<string>();
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                var path = http.Request.Path.Value!;
                var at = path.IndexOf("/functions", StringComparison.Ordinal);
                if (databaseId.Length == 0 || at < 0)
                {
                    return Task.FromResult<IResult?>(null);
                }

                requests.Enqueue(http.Request.Method + " " + path[at..] + http.Request.QueryString +
                    (http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ? "" : " anonymous"));
                if (mode == "missing")
                {
                    return Task.FromResult<IResult?>(Results.NotFound());
                }

                if (mode == "unavailable")
                {
                    return Task.FromResult<IResult?>(Results.StatusCode(503));
                }

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
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var secret = await HttpJourney.CreatePatAsync(client, "Functions MCP", account.Password);
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
            var emptyTools = await CallAsync("list_functions", new { databaseId });
            await Assert.That(emptyTools.GetProperty("functions").GetArrayLength()).IsEqualTo(0);
            await Assert.That(emptyTools.GetProperty("hasMore").GetBoolean()).IsFalse();

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

            var inventory = (await HttpJourney.McpAsync(client, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools")
                .EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
            var tools = new[] { "list_functions", "get_function", "list_function_logs", "list_function_deliveries", "delete_function" };
            foreach (var tool in tools)
            {
                await Assert.That(inventory.ContainsKey(tool)).IsTrue();
                await Assert.That(inventory[tool].TryGetProperty("outputSchema", out _)).IsTrue();
                await Assert.That(inventory[tool].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()).IsEqualTo(tool == "delete_function");
            }
            await Assert.That(inventory["delete_function"].GetProperty("inputSchema").GetProperty("required")
                .EnumerateArray().Select(item => item.GetString())).Contains("confirmName");
            var first = await CallAsync("list_functions", new { databaseId, limit = 1 });
            await Assert.That(first.GetProperty("functions")[0].GetProperty("name").GetString()).IsEqualTo("hello");
            await Assert.That(first.GetProperty("hasMore").GetBoolean()).IsTrue();
            var next = await CallAsync("list_functions", new { databaseId, limit = 2, after = first.GetProperty("nextCursor").GetString() });
            await Assert.That(next.GetProperty("functions").GetArrayLength()).IsEqualTo(2);
            await Assert.That(next.GetProperty("nextCursor").ValueKind).IsEqualTo(JsonValueKind.Null);
            var function = await CallAsync("get_function", new { databaseId, name = "on-order" });
            await Assert.That(function.GetProperty("function").GetProperty("source").GetString()).Contains("function handler(event)");
            await Assert.That(function.GetProperty("function").GetProperty("trigger").GetProperty("collection").GetString()).IsEqualTo("orders");
            await Assert.That(function.GetProperty("invokeUrl").GetString()).EndsWith("/databases/fn-app/functions/on-order/invoke");
            var logs = await CallAsync("list_function_logs", new { databaseId, name = "on-order" });
            await Assert.That(logs.GetProperty("logs").GetArrayLength()).IsEqualTo(2);
            await Assert.That(logs.GetProperty("logs")[0].GetProperty("error").GetString()).IsEqualTo("TypeError: boom");
            var deliveries = await CallAsync("list_function_deliveries", new { databaseId, name = "on-order" });
            await Assert.That(deliveries.GetProperty("deliveries")[0].GetProperty("errorCode").GetString()).IsEqualTo("target_rejected");
            foreach (var tool in tools)
            {
                var arguments = Arguments(tool);
                arguments["projectId"] = "01JA0000000000000000000001";
                await ErrorAsync(tool, arguments);
            }
            await ErrorAsync("get_function", new { databaseId, name = "gone" });
            await ErrorAsync("list_functions", new { databaseId, limit = 101 });
            var beforeInvalid = requests.Count;
            foreach (var tool in tools.Where(tool => tool != "list_functions"))
            {
                await ErrorAsync(tool, Arguments(tool, "Bad Name"));
            }

            await ErrorAsync("delete_function", new { databaseId, name = "hello", confirmName = "wrong" });
            await Assert.That(requests.Count).IsEqualTo(beforeInvalid);

            mode = "unavailable";
            var unavailable = await PageAsync(path, HttpStatusCode.ServiceUnavailable);
            await Assert.That(unavailable).Contains("Functions are unavailable.");

            mode = "live";
            var form = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(client, path), ["databaseId"] = databaseId, ["name"] = "hello" };
            var noCsrf = new Dictionary<string, string>(form); noCsrf.Remove("_csrf");
            using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/functions/delete", noCsrf))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            await Assert.That(requests).DoesNotContain("DELETE /functions/hello");
            using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/functions/delete", form))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(deleted.Headers.Location!.ToString()).IsEqualTo(path);
            }
            await Assert.That(requests).Contains("DELETE /functions/hello");
            var deletedFunction = await CallAsync("delete_function", new { databaseId, name = "hello", confirmName = "hello" });
            await Assert.That(deletedFunction.GetProperty("deleted").GetBoolean()).IsTrue();
            await Assert.That(requests.Count(item => item == "DELETE /functions/hello")).IsEqualTo(2);
            using (var controls = scenario.CreateProviderClient())
            using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "viewer" }))
            {
                role.EnsureSuccessStatusCode();
            }

            using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/functions/delete", form))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            var beforeDenied = requests.Count;
            foreach (var tool in tools)
            {
                await ErrorAsync(tool, Arguments(tool));
            }

            await Assert.That(requests.Count).IsEqualTo(beforeDenied).Because("viewer requests never reach the functions API");

            Dictionary<string, object> Arguments(string tool, string name = "hello")
            {
                var arguments = new Dictionary<string, object> { ["databaseId"] = databaseId };
                if (tool != "list_functions")
                {
                    arguments["name"] = name;
                }

                if (tool == "delete_function")
                {
                    arguments["confirmName"] = name;
                }

                return arguments;
            }

            async Task<JsonElement> CallAsync(string name, object arguments)
            {
                var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
                var result = response.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(name);
                return result.GetProperty("structuredContent");
            }

            async Task ErrorAsync(string name, object arguments)
            {
                var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
                await Assert.That(response.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue().Because(name);
                await Assert.That(response.GetRawText()).DoesNotContain("An error occurred invoking");
            }

            async Task<string> PageAsync(string url, HttpStatusCode expected)
            {
                using var response = await client.GetAsync(url);
                await Assert.That(response.StatusCode).IsEqualTo(expected);
                return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            }
        }
    }
}
