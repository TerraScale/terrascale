using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DeclaredSchemaMcpJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task DeclaredValidationUsesOfficialSdkAndProjectAuthorityWithoutChangingInference()
        {
            TestAuth auth = null!;
            var organizationId = "";
            var projectId = "";
            var calls = 0;
            JsonElement? stored = null;
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
            {
                if (!http.Request.Path.Value!.EndsWith("/collections/users/schema", StringComparison.Ordinal))
                {
                    return null;
                }

                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId,
                    http.Request.Method == "PUT" ? "documents:write" : "documents:read");
                await Assert.That(http.Request.Path.Value).IsEqualTo("/v1/projects/" + projectId + "/databases/schema-mcp/collections/users/schema");
                Interlocked.Increment(ref calls);
                if (http.Request.Method == "PUT")
                {
                    await Assert.That(http.Request.ContentType).Contains("application/json");
                    using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                    var schema = body.RootElement;
                    await Assert.That(System.Text.Encoding.UTF8.GetByteCount(schema.GetRawText()) <= 64 * 1024).IsTrue();
                    if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$comment", out var comment) && comment.GetString() == "cancel")
                    {
                        waiting.TrySetResult();
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted); }
                        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { canceled.TrySetResult(); }
                        return Results.StatusCode(499);
                    }
                    if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var type) && type.GetString() == "invalid")
                    {
                        return Results.Json(new { error = "invalid_schema", message = "Unknown JSON Schema type." }, statusCode: 400);
                    }

                    stored = schema.Clone();
                }
                else
                {
                    await Assert.That(http.Request.Method).IsEqualTo("GET");
                }

                return Results.Json(new { schema = stored, dialect = stored.HasValue ? "https://json-schema.org/draft/2020-12/schema" : null });
            });
            using var client = host.CreateClient();
            using var replica = host.CreateClient(replica: true);
            using var controls = host.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var secret = await HttpJourney.CreatePatAsync(client, "Declared schema MCP", account.Password);
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
            projectId = identity.RootElement.GetProperty("projectId").GetString()!;
            var discovery = await HttpJourney.McpAsync(client, secret, "tools/list", new { });
            var tools = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
            await Assert.That(tools["set_collection_schema"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()).IsTrue();
            await Assert.That(tools["get_collection_schema"].GetProperty("description").GetString()).Contains("sampled fields");
            await Assert.That(tools["get_collection_validation"].GetProperty("outputSchema").GetProperty("properties").TryGetProperty("schema", out _)).IsTrue();
            foreach (var obsolete in new[] { "get_document_history", "list_index_recommendations", "apply_index_recommendation", "get_index_migration" })
            {
                await Assert.That(tools.ContainsKey(obsolete)).IsFalse();
                var refused = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = obsolete, arguments = new { } });
                await Assert.That(refused.GetRawText()).Contains(obsolete);
                await Assert.That(refused.GetRawText()).DoesNotContain("An error occurred invoking");
                await Assert.That(refused.TryGetProperty("error", out _) || refused.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            }
            var created = await Call("create_database", new { name = "schema-mcp", region = "GRU" });
            var databaseId = created.GetProperty("id").GetString()!;
            await Call("create_collection", new { databaseId, name = "users" });
            var detached = await Call("get_collection_validation", new { databaseId, collection = "users" });
            await Assert.That(detached.GetProperty("schema").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(detached.GetProperty("dialect").ValueKind).IsEqualTo(JsonValueKind.Null);
            foreach (var json in new[] { "{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"}}}", "true", "false",
                "{\"description\":\"" + new string('\u0080', 32000) + "\"}",
                "{\"description\":\"" + string.Concat(Enumerable.Repeat("\\u0080", 32000)) + "\"}" })
            {
                using var schema = JsonDocument.Parse(json);
                var applied = await Call("set_collection_schema", new { databaseId, collection = "users", schema = schema.RootElement });
                await Assert.That(applied.GetProperty("status").GetString()).IsEqualTo("applied");
                await Assert.That(JsonElement.DeepEquals(stored!.Value, schema.RootElement)).IsTrue();
                var declared = await Call("get_collection_validation", new { databaseId, collection = "users" });
                await Assert.That(JsonElement.DeepEquals(declared.GetProperty("schema"), schema.RootElement)).IsTrue();
                await Assert.That(declared.GetProperty("dialect").GetString()).IsEqualTo("https://json-schema.org/draft/2020-12/schema");
            }
            var inferred = await Call("get_collection_schema", new { databaseId, collection = "users" });
            await Assert.That(inferred.GetProperty("schema").TryGetProperty("fields", out _)).IsTrue();
            await Error(new { databaseId, collection = "users", schema = new { type = "invalid" } });
            await Assert.That(stored!.Value.GetProperty("description").GetString()).IsEqualTo(new string('\u0080', 32000));
            // Exact producer schema budget plus the MCP envelope must reach the SDK.
            var large = "{\"$comment\":\"" + new string('x', 64 * 1024 - 15) + "\"}";
            await Assert.That(System.Text.Encoding.UTF8.GetByteCount(large)).IsEqualTo(64 * 1024);
            using (var edge = JsonDocument.Parse(large))
            {
                await Call("set_collection_schema", new { databaseId, collection = "users", schema = edge.RootElement });
            }

            var beforeDenials = calls;
            foreach (var invalid in new[] { "null", "[]", "42", "\"object\"" })
            {
                using var schema = JsonDocument.Parse(invalid);
                await Error(new { databaseId, collection = "users", schema = schema.RootElement });
            }
            await Error(new { databaseId, collection = "users", schema = new { description = new string('x', 64 * 1024) } });
            await Error(new { databaseId, collection = "users", schema = new { description = new string('\u0080', 32 * 1024) } });
            await Error(new { databaseId, collection = "invalid/name", schema = true });
            await Error(new { databaseId = AccountService.NewId(), collection = "users", schema = true });
            await Error(new { databaseId, collection = "users", schema = true, organizationId = AccountService.NewId() });
            await Error(new { databaseId, collection = "users", schema = true, projectId = AccountService.NewId() });
            foreach (var role in new[] { "member", "viewer" })
            {
                using var changed = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = role });
                changed.EnsureSuccessStatusCode();
                await Error(new { databaseId, collection = "users", schema = true });
            }
            await Assert.That(calls).IsEqualTo(beforeDenials);
            using (var admin = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "admin" }))
            {
                admin.EnsureSuccessStatusCode();
            }

            await Call("set_collection_schema", new { databaseId, collection = "users", schema = true });

            // Disconnect an actual HTTP MCP caller after the producer receives its PUT.
            using var cancellation = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
            request.Headers.Add("Mcp-Method", "tools/call");
            request.Headers.Add("Mcp-Name", "set_collection_schema");
            request.Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                { name = "set_collection_schema", arguments = new { databaseId, collection = "users", schema = new Dictionary<string, string> { ["$comment"] = "cancel" } } }
            });
            var pending = client.SendAsync(request, cancellation.Token);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            try { using var unexpected = await pending; throw new InvalidOperationException("Canceled MCP request completed."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var owner = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "owner" }))
            {
                owner.EnsureSuccessStatusCode();
            }

            await Call("delete_database", new { databaseId, confirmName = "schema-mcp" });

            async Task<JsonElement> Call(string name, object arguments)
            {
                var response = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name, arguments });
                var result = response.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(name + ": " + result);
                var value = result.GetProperty("structuredContent");
                if (tools[name].GetProperty("outputSchema").TryGetProperty("required", out var required))
                {
                    foreach (var field in required.EnumerateArray())
                    {
                        await Assert.That(value.TryGetProperty(field.GetString()!, out _)).IsTrue().Because(name + " output requires " + field.GetString());
                    }
                }

                return value;
            }

            async Task Error(object arguments)
            {
                var response = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "set_collection_schema", arguments });
                await Assert.That(response.TryGetProperty("error", out _) || response.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            }
        }
    }
}
