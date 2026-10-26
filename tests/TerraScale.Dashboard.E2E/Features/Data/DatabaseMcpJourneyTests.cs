using System.Net;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardE2EFixture>]
public sealed class DatabaseMcpJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task LegacyDatabaseDataAndCredentialToolsWorkAcrossReplicas()
    {
        using var client = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        using var login = await HttpJourney.LoginAsync(client);
        using var replicaLogin = await HttpJourney.LoginAsync(replica);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var patName = "DB E2E " + suffix;
        var secret = await HttpJourney.CreatePatAsync(client, patName);
        using var selectedResponse = await client.GetAsync("/api/v1/auth/me");
        await Assert.That(selectedResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var selected = await ReadJsonAsync(selectedResponse);
        var organizationId = selected.GetProperty("organizationId").GetString()!;
        var projectId = selected.GetProperty("projectId").GetString()!;
        var databaseId = "";
        var databaseName = "e2e-" + suffix;
        var keyId = "";
        var savedIds = new List<string>();
        var journeyPassed = false;
        try
        {
            var initialized = await HttpJourney.McpAsync(replica, secret, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "TUnit database E2E", version = "1" } });
            await Assert.That(initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()).IsEqualTo("terrascale-dashboard");
            var rootInitialized = await HttpJourney.McpAsync(replica, secret, "initialize",
                new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "Dedicated host", version = "1" } }, "/", "mcp.terrascale.tech");
            await Assert.That(rootInitialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()).IsEqualTo("terrascale-dashboard");
            var rootTools = await HttpJourney.McpAsync(replica, secret, "tools/list", new { }, "/", "mcp.terrascale.tech");
            await Assert.That(rootTools.GetProperty("result").GetProperty("tools").GetArrayLength()).IsEqualTo(99);
            var current = await HttpJourney.McpAsync(replica, secret, "server/discover", new { });
            await Assert.That(current.GetProperty("result").GetProperty("supportedVersions").EnumerateArray()
                .Any(version => version.GetString() == "2026-07-28")).IsTrue();
            var discovery = await HttpJourney.McpAsync(client, secret, "tools/list", new { });
            var tools = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
            var names = new[] { "list_databases", "get_database", "create_database", "set_database_regions", "branch_database", "delete_database", "list_collections", "create_collection", "list_documents", "get_document", "create_document", "replace_document", "patch_document", "delete_document", "get_document_history", "get_collection_schema", "set_collection_schema", "list_indexes", "create_index", "list_index_recommendations", "apply_index_recommendation", "get_index_migration", "check_sql", "run_sql", "list_saved_queries", "save_query", "delete_saved_query", "list_api_keys", "create_api_key", "create_m2m_client", "rotate_api_key", "revoke_api_key", "set_api_key_allowed_cidrs" };
            await Assert.That(names.All(tools.ContainsKey)).IsTrue();
            foreach (var name in names)
            {
                var annotations = tools[name].GetProperty("annotations");
                await Assert.That(annotations.GetProperty("openWorldHint").GetBoolean()).IsFalse();
                await Assert.That(tools[name].TryGetProperty("outputSchema", out _)).IsTrue();
                if (annotations.TryGetProperty("readOnlyHint", out var readOnly) && readOnly.GetBoolean())
                {
                    await Assert.That(annotations.GetProperty("destructiveHint").GetBoolean()).IsFalse();
                    await Assert.That(annotations.GetProperty("idempotentHint").GetBoolean()).IsTrue();
                }
            }
            var documentInputs = tools["list_documents"].GetProperty("inputSchema").GetProperty("properties");
            await Assert.That(documentInputs.TryGetProperty("limit", out _) && documentInputs.TryGetProperty("after", out _)).IsTrue();
            var patchInputs = tools["patch_document"].GetProperty("inputSchema").GetProperty("properties");
            await Assert.That(patchInputs.TryGetProperty("documentId", out _) && patchInputs.TryGetProperty("patch", out _)).IsTrue();
            var patchSchema = patchInputs.GetProperty("patch");
            await Assert.That(patchSchema.GetProperty("type").ToString().Contains("object", StringComparison.Ordinal)).IsTrue();

            var created = await CallAsync(client, "create_database", new() { ["name"] = databaseName, ["regions"] = new[] { "GRU", "SFO" } });
            databaseId = created.GetProperty("id").GetString()!;
            await Assert.That(created.GetProperty("name").GetString()).IsEqualTo(databaseName);
            await Assert.That(created.GetProperty("placement").GetArrayLength()).IsEqualTo(2);
            await Assert.That(created.TryGetProperty("projectId", out _)).IsFalse();
            var listed = await CallAsync(replica, "list_databases");
            await Assert.That(listed.GetProperty("databases").EnumerateArray().Any(db => db.GetProperty("id").GetString() == databaseId)).IsTrue();
            await ErrorAsync(replica, "create_database", new() { ["name"] = "Invalid_Name" });
            await ErrorAsync(replica, "get_database", new() { ["databaseId"] = databaseId, ["projectId"] = Guid.NewGuid().ToString() });
            await ErrorAsync(replica, "set_database_regions", new() { ["databaseId"] = databaseId, ["regions"] = new[] { "SFO" } });
            var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + databaseId + "/documents");
            await ErrorAsync(client, "create_document", new() { ["databaseId"] = databaseId, ["collection"] = "users", ["document"] = new { name = "Before collection" } });
            var createdCollection = await CallAsync(client, "create_collection", new() { ["databaseId"] = databaseId, ["name"] = "users" });
            await Assert.That(createdCollection.GetProperty("collection").GetString()).IsEqualTo("users");
            await ErrorAsync(replica, "create_collection", new() { ["databaseId"] = databaseId, ["name"] = "Bad Name" });
            var collections = await CallAsync(replica, "list_collections", new() { ["databaseId"] = databaseId });
            await Assert.That(collections.GetProperty("collections")[0].GetString()).IsEqualTo("users");
            var docFields = new Dictionary<string, object?> { ["databaseId"] = databaseId, ["collection"] = "users" };
            for (var index = 0; index < 31; index++)
                await CallAsync(client, "create_document", new(docFields) { ["documentId"] = "01K78CVSTMER000000000000" + index.ToString("D2"), ["document"] = new { name = "Customer " + index, score = index, profile = new { city = "GRU", remove = true } } });
            var duplicateFields = new Dictionary<string, object?>(docFields) { ["organizationId"] = organizationId, ["projectId"] = projectId, ["documentId"] = "01K78CVSTMER00000000000000", ["document"] = new { name = "Duplicate" } };
            var duplicate = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "create_document", arguments = duplicateFields });
            await Assert.That(duplicate.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await Assert.That(duplicate.GetRawText()).Contains("already exists");
            var documents = await CallAsync(replica, "list_documents", new(docFields));
            await Assert.That(documents.GetProperty("documents").GetArrayLength()).IsEqualTo(31);
            var firstDocument = documents.GetProperty("documents")[0];
            await Assert.That(firstDocument.TryGetProperty("value", out _) && !firstDocument.TryGetProperty("version", out _)).IsTrue();
            var page = await CallAsync(replica, "list_documents", new(docFields) { ["limit"] = 3, ["after"] = "3" });
            await Assert.That(page.GetProperty("documents").GetArrayLength()).IsEqualTo(3);
            await Assert.That(page.GetProperty("documents")[0].GetProperty("id").GetString()).IsEqualTo("01K78CVSTMER00000000000003");
            var search = await CallAsync(replica, "list_documents", new(docFields) { ["limit"] = 3, ["after"] = "3", ["search"] = "CUSTOMER 4" });
            await Assert.That(search.GetProperty("documents").GetArrayLength()).IsEqualTo(1);
            var empty = await CallAsync(replica, "list_documents", new(docFields) { ["limit"] = 3, ["search"] = "CUSTOMER 4" });
            await Assert.That(empty.GetProperty("documents").GetArrayLength()).IsEqualTo(0);
            await Assert.That(empty.GetProperty("hasMore").GetBoolean()).IsTrue();
            var continued = await CallAsync(replica, "list_documents", new(docFields) { ["limit"] = 3, ["search"] = "CUSTOMER 4", ["after"] = empty.GetProperty("nextCursor").GetString() });
            await Assert.That(continued.GetProperty("documents").GetArrayLength()).IsEqualTo(1);
            await Assert.That(documents.GetProperty("hasMore").GetBoolean()).IsFalse();
            var browser = await CallAsync(replica, "list_documents", new(docFields) { ["page"] = 2 });
            await Assert.That(browser.GetProperty("page").GetInt32()).IsEqualTo(2);
            await Assert.That(browser.GetProperty("pageCount").GetInt32()).IsEqualTo(2);
            await Assert.That(browser.GetProperty("total").GetInt32()).IsEqualTo(31);
            await Assert.That(browser.GetProperty("totalIsLowerBound").GetBoolean()).IsFalse();
            await Assert.That(browser.GetProperty("hasMore").GetBoolean()).IsFalse();
            await ErrorAsync(replica, "list_documents", new(docFields) { ["limit"] = 3, ["page"] = 2 });
            await ErrorAsync(replica, "list_documents", new(docFields) { ["after"] = "3", ["sort"] = "name" });
            await ErrorAsync(replica, "list_documents", new(docFields) { ["limit"] = 101 });
            using (var apiDocuments = await replica.GetAsync("/api/v1/dashboard/data/documents?databaseId=" + databaseId + "&collection=users"))
                await Assert.That((await ReadJsonAsync(apiDocuments)).GetProperty("documents").GetArrayLength()).IsEqualTo(31);

            var reference = new Dictionary<string, object?>(docFields) { ["documentId"] = "01K78CVSTMER00000000000000" };
            var original = await CallAsync(replica, "get_document", new(reference) { ["includeVersion"] = true });
            var originalVersion = original.GetProperty("version").GetString()!;
            var patched = await CallAsync(client, "patch_document", new(reference) { ["patch"] = new { profile = new { city = "SFO", remove = (object?)null } } });
            await Assert.That(patched.GetProperty("operation").GetString()).IsEqualTo("patch");
            var latest = await CallAsync(replica, "get_document", new(reference));
            await Assert.That(latest.GetProperty("value").GetProperty("profile").GetProperty("city").GetString()).IsEqualTo("SFO");
            await Assert.That(latest.GetProperty("value").GetProperty("profile").TryGetProperty("remove", out _)).IsFalse();
            await Assert.That(latest.TryGetProperty("version", out _)).IsFalse();
            await ErrorAsync(replica, "replace_document", new(reference) { ["document"] = new { name = "Lost update" }, ["version"] = originalVersion });
            await ErrorAsync(replica, "patch_document", new(reference) { ["patch"] = new { name = "Invalid version" }, ["version"] = "" });
            await ErrorAsync(replica, "get_document", new(reference) { ["id"] = "01K78CVSTMER00000000000099" });
            await ErrorAsync(client, "create_document", new(docFields) { ["documentId"] = "customer-00", ["document"] = new { name = "Not a ULID" } });
            using (var invalidId = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["collection"] = "users", ["operation"] = "create", ["id"] = "customer-00", ["document"] = "{}" }))
            {
                await Assert.That(invalidId.StatusCode).IsEqualTo((HttpStatusCode)422);
                await Assert.That(await invalidId.Content.ReadAsStringAsync()).Contains("ULID");
            }
            await CallAsync(client, "replace_document", new(docFields) { ["id"] = "01K78CVSTMER00000000000000", ["value"] = new { name = "Alias replacement", score = 100 } });
            using (var nativeWrite = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["collection"] = "users", ["operation"] = "replace", ["id"] = "01K78CVSTMER00000000000000", ["document"] = "{\"name\":\"Native replacement\",\"score\":101}" }))
            {
                await Assert.That(nativeWrite.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var result = await ReadJsonAsync(nativeWrite);
                await Assert.That(result.GetProperty("operation").GetString()).IsEqualTo("replace");
                await Assert.That(result.TryGetProperty("version", out _)).IsFalse();
            }
            var schema = await CallAsync(replica, "get_collection_schema", new(docFields));
            await Assert.That(schema.GetProperty("collection").GetString()).IsEqualTo("users");
            await Assert.That(schema.GetProperty("schema").GetProperty("fields").GetArrayLength()).IsGreaterThan(0);
            await CallAsync(client, "create_index", new(docFields) { ["field"] = "name" });
            await CallAsync(replica, "create_index", new(docFields) { ["field"] = "name" });
            var indexes = await CallAsync(replica, "list_indexes", new(docFields));
            await Assert.That(indexes.GetProperty("indexes").GetArrayLength()).IsEqualTo(1);
            await Assert.That(indexes.GetProperty("indexes")[0].GetProperty("field").GetString()).IsEqualTo("name");
            using (var apiIndexes = await replica.GetAsync("/api/v1/dashboard/indexes?databaseId=" + databaseId + "&collection=users"))
            {
                var result = await ReadJsonAsync(apiIndexes);
                await Assert.That(result[0].GetProperty("field").GetString()).IsEqualTo("name");
                await Assert.That(result[0].TryGetProperty("fields", out _)).IsFalse();
            }
            var detail = await CallAsync(replica, "get_database", new() { ["databaseId"] = databaseId });
            await Assert.That(detail.GetProperty("documents").GetInt64()).IsEqualTo(31);
            await Assert.That(detail.GetProperty("bytes").GetInt64()).IsGreaterThan(0);
            await Assert.That(detail.GetProperty("regions").GetArrayLength()).IsEqualTo(2);
            var analysis = await CallAsync(replica, "check_sql", new() { ["databaseId"] = databaseId, ["sql"] = "SELECT * FROM users WHERE score > $1 ORDER BY name LIMIT 20" });
            await Assert.That(analysis.GetProperty("ok").GetBoolean()).IsFalse();
            await Assert.That(analysis.GetProperty("error").GetString()).Contains("cannot bind parameters");
            analysis = await CallAsync(replica, "check_sql", new() { ["databaseId"] = databaseId, ["sql"] = "SELECT * FROM users WHERE score > 1 ORDER BY name LIMIT 20" });
            await Assert.That(analysis.GetProperty("ok").GetBoolean()).IsTrue();
            await Assert.That(analysis.GetProperty("parameterSlots").GetInt32()).IsEqualTo(0);
            await Assert.That(analysis.TryGetProperty("suggestions", out _)).IsFalse();
            await Assert.That(analysis.TryGetProperty("explain", out _)).IsFalse();
            var sql = await CallAsync(replica, "run_sql", new() { ["databaseId"] = databaseId, ["sql"] = "SELECT * FROM users LIMIT 100" });
            await Assert.That(sql.GetProperty("result").GetProperty("rows").GetArrayLength()).IsEqualTo(31);
            await ErrorAsync(replica, "run_sql", new() { ["databaseId"] = databaseId, ["sql"] = "DELETE FROM users" });
            await ErrorAsync(replica, "run_sql", new() { ["databaseId"] = databaseId, ["sql"] = "SELECT '" + new string('a', 16 * 1024) + "' FROM users" });
            for (var index = 0; index < 2; index++)
            {
                var saved = await CallAsync(index == 0 ? client : replica, "save_query", new() { ["name"] = "Shared " + suffix, ["sql"] = "SELECT * FROM users LIMIT 100" });
                savedIds.Add(saved.GetProperty("id").GetString()!);
            }
            await Assert.That(savedIds.Distinct().Count()).IsEqualTo(2);
            var queries = await CallAsync(replica, "list_saved_queries");
            await Assert.That(savedIds.All(id => queries.GetProperty("queries").EnumerateArray().Any(query => query.GetProperty("id").GetString() == id))).IsTrue();
            var traversed = new List<string>();
            string? afterQuery = null;
            do
            {
                var queryPage = await CallAsync(replica, "list_saved_queries", new() { ["limit"] = 1, ["after"] = afterQuery ?? "" });
                var ids = queryPage.GetProperty("queries").EnumerateArray().Select(query => query.GetProperty("id").GetString()!).ToArray();
                await Assert.That(ids.Length).IsLessThanOrEqualTo(1);
                await Assert.That(ids.Any(traversed.Contains)).IsFalse();
                traversed.AddRange(ids);
                afterQuery = queryPage.GetProperty("nextCursor").GetString();
                await Assert.That(queryPage.GetProperty("hasMore").GetBoolean()).IsEqualTo(afterQuery is not null);
            } while (afterQuery is not null);
            await Assert.That(savedIds.All(traversed.Contains)).IsTrue();
            await ErrorAsync(replica, "list_saved_queries", new() { ["after"] = "bad" });
            await ErrorAsync(replica, "list_saved_queries", new() { ["limit"] = 0 });
            await ErrorAsync(replica, "list_saved_queries", new() { ["limit"] = 101 });
            var checkpoint = await CallAsync(replica, "list_saved_queries", new() { ["limit"] = 1 });
            var cursor = checkpoint.GetProperty("nextCursor").GetString()!;
            await ErrorAsync(replica, "list_databases", new() { ["after"] = cursor });
            var removed = checkpoint.GetProperty("queries")[0].GetProperty("id").GetString()!;
            await Assert.That(savedIds.Contains(removed)).IsTrue();
            await CallAsync(replica, "delete_saved_query", new() { ["queryId"] = removed });
            savedIds.Remove(removed);
            var resumed = await CallAsync(replica, "list_saved_queries", new() { ["limit"] = 2, ["after"] = cursor });
            await Assert.That(resumed.GetProperty("queries").EnumerateArray().Select(query => query.GetProperty("id").GetString()!))
                .IsEquivalentTo(traversed.Where(id => StringComparer.Ordinal.Compare(id, removed) > 0));
            await Assert.That(resumed.GetProperty("nextCursor").ValueKind).IsEqualTo(JsonValueKind.Null);
            using (var apiQueries = await replica.GetAsync("/api/v1/dashboard/data/saved-queries"))
                await Assert.That((await ReadJsonAsync(apiQueries)).EnumerateArray().Any(query => query.GetProperty("id").GetString() == savedIds[0])).IsTrue();
            foreach (var id in savedIds.ToArray()) await CallAsync(replica, "delete_saved_query", new() { ["queryId"] = id });
            savedIds.Clear();

            var issued = await CallAsync(client, "create_api_key", new() { ["name"] = "SDK " + suffix, ["scopes"] = new[] { "database:read", "database:write" }, ["databaseId"] = databaseId });
            keyId = issued.GetProperty("apiKey").GetProperty("keyId").GetString()!;
            var keySecret = issued.GetProperty("secret").GetString()!;
            await Assert.That(issued.GetProperty("apiKey").GetProperty("lastFour").GetString()).IsEqualTo("");
            await Assert.That(issued.GetProperty("apiKey").GetProperty("databaseName").GetString()).IsEqualTo(databaseName);
            var credentials = await CallAsync(replica, "list_api_keys", new() { ["search"] = "SDK " + suffix, ["databaseId"] = databaseId });
            var publicKey = credentials.GetProperty("credentials").EnumerateArray().Single(key => key.GetProperty("keyId").GetString() == keyId);
            await Assert.That(publicKey.GetProperty("kind").GetString()).IsEqualTo("api-key");
            await Assert.That(publicKey.GetProperty("isActive").GetBoolean()).IsTrue();
            await Assert.That(credentials.GetRawText().Contains(keySecret, StringComparison.Ordinal)).IsFalse();
            await Assert.That((await CallAsync(replica, "list_api_keys", new() { ["kind"] = "m2m" })).GetProperty("credentials").GetArrayLength()).IsEqualTo(0);
            await ErrorAsync(replica, "list_api_keys", new() { ["kind"] = "invalid" });
            var rotated = await CallAsync(replica, "rotate_api_key", new() { ["keyId"] = keyId, ["previousSecretTtlSeconds"] = 3600 });
            await Assert.That(rotated.GetProperty("apiKey").GetProperty("keyId").GetString()).IsEqualTo(keyId);
            await Assert.That(rotated.GetProperty("apiKey").GetProperty("databaseName").GetString()).IsEqualTo(databaseName);
            var rotatedSecret = rotated.GetProperty("secret").GetString()!;
            await Assert.That(rotatedSecret.StartsWith(keyId + "_", StringComparison.Ordinal) && rotatedSecret != keySecret).IsTrue();
            // The previous secret is still in its grace period, so ts-auth refuses another rotation.
            await ErrorAsync(replica, "rotate_api_key", new() { ["keyId"] = keyId });
            await ErrorAsync(replica, "rotate_api_key", new() { ["keyId"] = keyId, ["previousSecretTtlSeconds"] = 86401 });
            await ErrorAsync(replica, "create_m2m_client", new() { ["name"] = "Unavailable", ["scopes"] = new[] { "database:read" } });
            await ErrorAsync(replica, "get_document_history", new(reference));
            await ErrorAsync(replica, "set_collection_schema", new(docFields) { ["schema"] = new { name = "users" } });
            await ErrorAsync(replica, "list_index_recommendations", new() { ["databaseId"] = databaseId });
            await ErrorAsync(replica, "get_index_migration", new(docFields));
            using (var authConsole = await replica.GetAsync("/auth/overview"))
            {
                await Assert.That(authConsole.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await authConsole.Content.ReadAsStringAsync();
                await Assert.That(html).Contains($"id=\"auth-issuer\">https://auth.terrascale.tech/{projectId}</code>");
                await Assert.That(html).Contains($"https://auth.terrascale.tech/ui/{projectId}/sign-in");
            }
            await ErrorAsync(replica, "delete_database", new() { ["databaseId"] = databaseId, ["confirmName"] = "wrong-name" });
            await CallAsync(replica, "revoke_api_key", new() { ["keyId"] = keyId });
            keyId = "";
            await CallAsync(replica, "delete_document", new(reference));
            var remaining = await CallAsync(client, "list_documents", new(docFields));
            await Assert.That(remaining.GetProperty("documents").GetArrayLength()).IsEqualTo(30);
            await CallAsync(replica, "delete_database", new() { ["databaseId"] = databaseId, ["confirmName"] = databaseName });
            databaseId = "";
            journeyPassed = true;
        }
        finally
        {
            foreach (var id in savedIds) await CleanupAsync("delete_saved_query", new() { ["queryId"] = id });
            if (keyId.Length > 0) await CleanupAsync("revoke_api_key", new() { ["keyId"] = keyId });
            if (databaseId.Length > 0) await CleanupAsync("delete_database", new() { ["databaseId"] = databaseId, ["confirmName"] = databaseName });
            try
            {
                var tokens = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "list_access_tokens", arguments = new { } });
                var rows = tokens.GetProperty("result").GetProperty("structuredContent").GetProperty("accessTokens");
                var token = rows.EnumerateArray().Single(item => item.GetProperty("name").GetString() == patName);
                using var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/access-tokens/" + token.GetProperty("id").GetString() + "/revoke", new() { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/profile?tab=access-tokens") });
                await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }
            catch (Exception error) { if (journeyPassed) throw; Console.Error.WriteLine("E2E PAT cleanup: " + error.Message); }
        }

        async Task<JsonElement> CallAsync(HttpClient caller, string name, Dictionary<string, object?>? fields = null)
        {
            var arguments = fields ?? new();
            arguments.TryAdd("organizationId", organizationId);
            arguments.TryAdd("projectId", projectId);
            var rpc = await HttpJourney.McpAsync(caller, secret, "tools/call", new { name, arguments });
            var result = rpc.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            return result.GetProperty("structuredContent");
        }
        async Task ErrorAsync(HttpClient caller, string name, Dictionary<string, object?> fields)
        {
            fields.TryAdd("organizationId", organizationId);
            fields.TryAdd("projectId", projectId);
            var rpc = await HttpJourney.McpAsync(caller, secret, "tools/call", new { name, arguments = fields });
            await Assert.That(rpc.TryGetProperty("error", out _) || rpc.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            // The SDK's fallback text hides the cause; every refusal must carry the dashboard's own message.
            await Assert.That(rpc.GetRawText()).DoesNotContain("An error occurred invoking");
        }
        async Task CleanupAsync(string name, Dictionary<string, object?> fields)
        { try { await CallAsync(client, name, fields); } catch (Exception error) { Console.Error.WriteLine("E2E cleanup " + name + ": " + error.Message); } }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
