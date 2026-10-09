using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraBase.Fast.Documents;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ApiReferenceTests
{
    // Every operation of the public document with its security scheme and required JWT scopes. Update it with every public endpoint;
    // an admin-listener operation appearing here is a leak.
    private const string Operations = """
        DELETE /projects/{project}/databases/{database}/collections/{collection}/schema TsAuth(documents:write) deleteDeclaredCollectionSchema Collections
        DELETE /v1/projects/{project}/backup-destination TsAuth(databases:write) deleteBackupDestination Backups
        DELETE /v1/projects/{project}/databases/{database} TsAuth(databases:write) deleteDatabase Databases
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection} TsAuth(documents:write) dropCollection Collections
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:write) deleteDocument Documents
        DELETE /v1/projects/{project}/databases/{database}/indexes/{index} TsAuth(databases:write) dropIndex Indexes
        DELETE /v1/projects/{project}/saved-queries/{query} TsAuth(databases:write) deleteSavedQuery Queries
        GET /healthz anonymous healthz Health
        GET /projects/{project}/analytics TsAuth(databases:read) getProjectAnalytics Analytics
        GET /projects/{project}/databases/{database}/analytics TsAuth(databases:read) getDatabaseAnalytics Analytics
        GET /projects/{project}/databases/{database}/auto-index/{collection} TsAuth(databases:read) getAutoIndexPolicy Indexes
        GET /projects/{project}/databases/{database}/collections/{collection}/changes TsAuth(documents:read) getCollectionChanges Documents
        GET /projects/{project}/databases/{database}/collections/{collection}/schema TsAuth(documents:read) getDeclaredCollectionSchema Collections
        GET /projects/{project}/databases/{database}/collections/{collection}/subscribe TsAuth(shapes:read) subscribeCollection Live
        GET /projects/{project}/databases/{database}/replication TsAuth(databases:read) getDatabaseReplication Replication
        GET /projects/{project}/replication TsAuth(databases:read) getProjectReplication Replication
        GET /readyz anonymous readyz Health
        GET /v1/projects/{project}/backup-destination TsAuth(databases:read) getBackupDestination Backups
        GET /v1/projects/{project}/databases TsAuth(databases:read) listDatabases Databases
        GET /v1/projects/{project}/databases/{database} TsAuth(databases:read) getDatabase Databases
        GET /v1/projects/{project}/databases/{database}/backup-policy TsAuth(databases:read) getBackupPolicy Backups
        GET /v1/projects/{project}/databases/{database}/backups TsAuth(databases:read) listBackups Backups
        GET /v1/projects/{project}/databases/{database}/collections TsAuth(documents:read) listCollections Collections
        GET /v1/projects/{project}/databases/{database}/collections/{collection} TsAuth(documents:read) getCollection Collections
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/documents TsAuth(documents:read) listDocuments Documents
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:read) getDocument Documents
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape TsAuth(shapes:read) getShape Live
        GET /v1/projects/{project}/databases/{database}/index-recommendations TsAuth(databases:read) listIndexRecommendations Indexes
        GET /v1/projects/{project}/databases/{database}/indexes TsAuth(databases:read) listIndexes Indexes
        GET /v1/projects/{project}/databases/{database}/point-in-time TsAuth(databases:read) getRestoreWindow Backups
        GET /v1/projects/{project}/databases/{database}/query-shapes TsAuth(databases:read) listQueryShapes Queries
        GET /v1/projects/{project}/databases/{database}/schema TsAuth(databases:read) getDatabaseSchema Schema
        GET /v1/projects/{project}/databases/{database}/schema/{collection} TsAuth(databases:read) getCollectionSchema Schema
        GET /v1/projects/{project}/databases/{database}/usage TsAuth(databases:read) getDatabaseUsage Usage
        GET /v1/projects/{project}/saved-queries TsAuth(databases:read) listSavedQueries Queries
        GET /v1/projects/{project}/saved-queries/{query} TsAuth(databases:read) getSavedQuery Queries
        GET /v1/projects/{project}/usage TsAuth(databases:read) getProjectUsage Usage
        PATCH /v1/projects/{project}/databases/{database} TsAuth(databases:write) updateDatabase Databases
        PATCH /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:write) patchDocument Documents
        POST /projects/{project}/databases/{database}/sql/explain TsAuth(databases:read) explainSql Queries
        POST /v1/projects/{project}/databases TsAuth(databases:write) createDatabase Databases
        POST /v1/projects/{project}/databases/{database}/backups TsAuth(databases:write) createBackup Backups
        POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore TsAuth(databases:write) restoreBackup Backups
        POST /v1/projects/{project}/databases/{database}/branches TsAuth(databases:write) branchDatabase Databases
        POST /v1/projects/{project}/databases/{database}/collections TsAuth(documents:write) createCollection Collections
        POST /v1/projects/{project}/databases/{database}/collections/{collection}/documents TsAuth(documents:write) createDocument Documents
        POST /v1/projects/{project}/databases/{database}/collections/{collection}/query TsAuth(documents:read) queryDocuments Queries
        POST /v1/projects/{project}/databases/{database}/index-recommendations/{recommendation}/apply TsAuth(databases:write) applyIndexRecommendation Indexes
        POST /v1/projects/{project}/databases/{database}/indexes TsAuth(databases:write) createIndex Indexes
        POST /v1/projects/{project}/databases/{database}/point-in-time/plan TsAuth(databases:read) planPointInTime Backups
        POST /v1/projects/{project}/databases/{database}/point-in-time/restore TsAuth(databases:write) restorePointInTime Backups
        POST /v1/projects/{project}/databases/{database}/sql TsAuth(databases:read) runSql Queries
        POST /v1/projects/{project}/databases/{database}/sql/check TsAuth(databases:read) checkSql Queries
        POST /v1/projects/{project}/databases/{database}/transactions TsAuth(documents:write) runTransaction Transactions
        POST /v1/projects/{project}/saved-queries TsAuth(databases:write) createSavedQuery Queries
        PUT /projects/{project}/databases/{database}/auto-index/{collection} TsAuth(databases:write) setAutoIndexPolicy Indexes
        PUT /projects/{project}/databases/{database}/collections/{collection}/schema TsAuth(documents:write) putDeclaredCollectionSchema Collections
        PUT /v1/projects/{project}/backup-destination TsAuth(databases:write) setBackupDestination Backups
        PUT /v1/projects/{project}/databases/{database}/backup-policy TsAuth(databases:write) setBackupPolicy Backups
        PUT /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:write) putDocument Documents
        PUT /v1/projects/{project}/databases/{database}/schema/{collection} TsAuth(databases:write) declareCollectionSchema Schema
        PUT /v1/projects/{project}/saved-queries/{query} TsAuth(databases:write) updateSavedQuery Queries
        """;

    [Test]
    public async Task The_root_redirects_to_scalar_and_the_reference_is_served_on_the_client_listener_only()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);

        using var root = await client.GetAsync(Uri("/"));
        using var page = await client.GetAsync(Uri("/scalar/"));
        using var script = await client.GetAsync(Uri("/scalar/scalar.js"));

        await Assert.That(root.StatusCode).IsEqualTo(HttpStatusCode.Found);
        // Relative, so it holds on every regional host and behind proxies.
        await Assert.That(root.Headers.Location!.IsAbsoluteUri).IsFalse();
        await Assert.That(root.Headers.Location.OriginalString).IsEqualTo("scalar/");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.Content.Headers.ContentType!.MediaType).IsEqualTo("text/html");
        await Assert.That(await page.Content.ReadAsStringAsync()).Contains("openapi/v1.json");
        await Assert.That(script.StatusCode).IsEqualTo(HttpStatusCode.OK);
        foreach (var path in new[] { "/", "/scalar", "/scalar/", "/scalar/scalar.js", "/openapi/v1.json" })
        {
            using var admin = await server.Client.GetAsync(Uri(path));
            await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task The_openapi_document_lists_every_public_operation_with_its_security_and_errors()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = "gru-1";
        });
        using var client = tls.CreateClient(server.ClientAddress!);

        using var response = await client.GetAsync(Uri("/openapi/v1.json"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var document = json.RootElement;
        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Value.TryGetProperty("responses", out _))
                .Select(operation => (Line: $"{operation.Name.ToUpperInvariant()} {path.Name} {Security(operation.Value)} {operation.Value.GetProperty("operationId").GetString()} {operation.Value.GetProperty("tags")[0].GetString()}", Operation: operation.Value)))
            .ToList();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(document.GetProperty("paths").EnumerateObject().Any(path =>
            path.Name.StartsWith("/management/", StringComparison.OrdinalIgnoreCase)
            || path.Name.StartsWith("/v1/management/", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(document.GetProperty("info").GetProperty("version").GetString()).IsEqualTo(ServiceVersion.Value);
        var server0 = document.GetProperty("servers")[0];
        await Assert.That(server0.GetProperty("url").GetString()).IsEqualTo("/");
        await Assert.That(server0.GetProperty("description").GetString()).Contains("gru-1");
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        await Assert.That(schemes.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("type").GetString()).IsEqualTo("http");
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("scheme").GetString()).IsEqualTo("bearer");
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("bearerFormat").GetString()).IsEqualTo("JWT");
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("description").GetString()).Contains("project access token requests receive 401");
        await Assert.That(string.Join(',', document.GetProperty("x-tagGroups").EnumerateArray().Select(group => group.GetProperty("name").GetString())))
            .IsEqualTo("Management,Data,System");
        await Assert.That(string.Join('\n', operations.Select(operation => operation.Line).Order(StringComparer.Ordinal)))
            .IsEqualTo(Operations.ReplaceLineEndings("\n"));
        const string project = "/v1/projects/{project}";
        var paths = document.GetProperty("paths");
        foreach (var (path, method, statuses) in new[]
        {
            (project + "/backup-destination", "get", "200,400,401,403,404,421"),
            (project + "/backup-destination", "put", "200,400,401,403,404,421,503"),
            (project + "/backup-destination", "delete", "204,400,401,403,404,421,503"),
            (project + "/databases/{database}/point-in-time", "get", "200,400,401,403,404,421,503"),
            (project + "/databases/{database}/point-in-time/restore", "post", "201,400,401,403,404,409,421,503"),
        })
        {
            await Assert.That(string.Join(',', paths.GetProperty(path).GetProperty(method).GetProperty("responses")
                .EnumerateObject().Select(status => status.Name).Order(StringComparer.Ordinal)))
                .IsEqualTo(statuses).Because($"{method} {path}");
        }

        var destination = paths.GetProperty(project + "/backup-destination");
        await Assert.That(destination.GetProperty("delete").GetProperty("responses").GetProperty("204").TryGetProperty("content", out _)).IsFalse();
        await Assert.That(destination.GetProperty("put").GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString())
            .IsEqualTo("#/components/schemas/ProjectBackupDestinationRequest");
        foreach (var method in new[] { "get", "put" })
        {
            var success = destination.GetProperty(method).GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json");
            await Assert.That(success.GetProperty("schema").GetProperty("oneOf").EnumerateArray()
                .Single(schema => schema.TryGetProperty("$ref", out _)).GetProperty("$ref").GetString())
                .IsEqualTo("#/components/schemas/ProjectBackupDestination");
            await Assert.That(success.GetProperty("example").EnumerateObject().Select(property => property.Name).ToList())
                .IsEquivalentTo(["endpoint", "region", "bucket", "mode", "updatedAt"]);
        }

        var schemas = document.GetProperty("components").GetProperty("schemas");
        // Credentials belong only to the request; responses also exclude the internal Vault secret reference.
        await Assert.That(schemas.GetProperty("ProjectBackupDestinationRequest").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).ToList())
            .IsEquivalentTo(["endpoint", "region", "bucket", "accessKeyId", "secretKey", "mode"]);
        await Assert.That(schemas.GetProperty("ProjectBackupDestination").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).ToList())
            .IsEquivalentTo(["endpoint", "region", "bucket", "mode", "updatedAt"]);
        foreach (var name in new[] { "PointInTimeRestoreRequest", "PointInTimePlanRequest" })
        {
            var request = schemas.GetProperty(name);
            await Assert.That(request.GetProperty("properties").TryGetProperty("destination", out _)).IsTrue().Because(name);
            await Assert.That(request.TryGetProperty("required", out var required)
                && required.EnumerateArray().Any(property => property.GetString() == "destination")).IsFalse().Because(name);
        }
        var selector = paths.GetProperty(project + "/databases/{database}/point-in-time").GetProperty("get")
            .GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "destination");
        await Assert.That(selector.GetProperty("in").GetString()).IsEqualTo("query");
        await Assert.That(selector.TryGetProperty("required", out var requiredSelector) && requiredSelector.GetBoolean()).IsFalse();
        await Assert.That(selector.GetProperty("schema").GetProperty("default").GetString()).IsEqualTo("platform");
        await Assert.That(selector.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToList())
            .IsEquivalentTo(["platform", "customer"]);

        foreach (var (line, operation) in operations)
        {
            await Assert.That(operation.GetProperty("tags").GetArrayLength()).IsEqualTo(1).Because(line);
            await Assert.That(string.IsNullOrEmpty(operation.GetProperty("summary").GetString())).IsFalse().Because(line);
            await Assert.That(string.IsNullOrEmpty(operation.GetProperty("description").GetString())).IsFalse().Because(line);
            if (operation.TryGetProperty("requestBody", out var requestBody))
            {
                foreach (var media in requestBody.GetProperty("content").EnumerateObject())
                {
                    await Assert.That(media.Value.TryGetProperty("example", out _)).IsTrue().Because(line);
                }
            }

            var responses = operation.GetProperty("responses").EnumerateObject().ToList();
            if (operation.GetProperty("operationId").GetString() is "getDeclaredCollectionSchema" or "putDeclaredCollectionSchema" or "deleteDeclaredCollectionSchema")
            {
                await Assert.That(responses.Any(status => status.Name == "503")).IsTrue().Because(line);
            }

            if (operation.GetProperty("operationId").GetString() == "putDeclaredCollectionSchema")
            {
                await Assert.That(responses.Any(status => status.Name == "415")).IsTrue().Because(line);
            }

            if (operation.GetProperty("operationId").GetString() is "getAutoIndexPolicy" or "setAutoIndexPolicy")
            {
                var example = operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                    .GetProperty("application/json").GetProperty("example");
                await Assert.That(example.GetProperty("collection").GetString()).IsEqualTo("people");
                await Assert.That(example.GetProperty("enabled").GetBoolean()).IsFalse();
                if (operation.GetProperty("operationId").GetString() == "setAutoIndexPolicy")
                {
                    var requestExample = operation.GetProperty("requestBody").GetProperty("content")
                        .GetProperty("application/json").GetProperty("example");
                    await Assert.That(requestExample.GetProperty("enabled").GetBoolean()).IsFalse();
                    await Assert.That(operation.GetProperty("responses").GetProperty("400").GetProperty("description").GetString())
                        .Contains("auto_index_policy_limit");
                    await Assert.That(operation.GetProperty("responses").GetProperty("403").GetProperty("description").GetString())
                        .Contains("forbidden_role");
                }
            }

            if (operation.GetProperty("operationId").GetString() is "getDocument" or "putDocument" or "patchDocument" or "deleteDocument")
            {
                var key = operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "id");
                await Assert.That(key.GetProperty("in").GetString()).IsEqualTo("query");
                await Assert.That(key.GetProperty("required").GetBoolean()).IsTrue();
                await Assert.That(key.GetProperty("schema").GetProperty("type").GetString()).IsEqualTo("string");
                await Assert.That(key.GetProperty("schema").GetProperty("maxLength").GetInt32()).IsEqualTo(256);
                await Assert.That(key.GetProperty("example").GetString()).IsEqualTo("a/b");
                await Assert.That(responses.Any(status => status.Name == "400")).IsTrue();
            }

            if (operation.GetProperty("operationId").GetString() is "getDatabaseUsage" or "getProjectUsage")
            {
                await Assert.That(operation.GetProperty("responses").GetProperty("400").GetProperty("description").GetString()).Contains("invalid_query");
            }

            if (operation.GetProperty("operationId").GetString() is "createDatabase" or "branchDatabase" or "restoreBackup"
                or "createBackup" or "createSavedQuery" or "createCollection" or "createDocument")
            {
                var success = responses.First(status => status.Name.StartsWith('2')).Value;
                var example = success.GetProperty("content").GetProperty("application/json").GetProperty("example");
                await Assert.That(EntityIds.IsValid(example.GetProperty("id").GetString())).IsTrue().Because(line);
            }

            // Every operation documents a success, and every error status the error envelope (or the readiness body).
            await Assert.That(responses.Any(status => status.Name.StartsWith('2'))).IsTrue().Because(line);
            foreach (var error in responses.Where(status => string.CompareOrdinal(status.Name, "400") >= 0))
            {
                await Assert.That(error.Value.GetProperty("content").GetProperty("application/json").TryGetProperty("example", out _)).IsTrue().Because($"{line} {error.Name}");
                var schema = error.Value.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetRawText();
                await Assert.That(schema.Contains("ErrorBody", StringComparison.Ordinal) || schema.Contains("\"error\"", StringComparison.Ordinal)
                    || (line.StartsWith("GET /readyz", StringComparison.Ordinal) && error.Name == "503" && schema.Contains("StatusBody", StringComparison.Ordinal))).IsTrue().Because($"{line} {error.Name}");
            }

            if (!line.Contains(" anonymous ", StringComparison.Ordinal))
            {
                await Assert.That(responses.Any(status => status.Name == "401")).IsTrue().Because(line);
                foreach (var scheme in operation.GetProperty("security").EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()))
                {
                    await Assert.That(scheme.Value.GetArrayLength()).IsEqualTo(0).Because(line);
                }

                var scopes = operation.GetProperty("x-required-scopes").EnumerateArray().Select(scope => scope.GetString()).ToArray();
                await Assert.That(scopes.Length).IsGreaterThan(0).Because(line);
                await Assert.That(operation.GetProperty("description").GetString()).Contains($"Required JWT scopes: {string.Join(", ", scopes)}.").Because(line);
            }
        }
    }

    [Test]
    public async Task Readiness_region_pin_errors_have_error_examples_while_unavailable_keeps_the_status_body()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = "gru-1";
        });
        using var client = tls.CreateClient(server.ClientAddress!);
        using var request = new HttpRequestMessage(HttpMethod.Get, Uri("/readyz"));
        request.Headers.Add("X-TerraScale-Region-Pin", "sfo-1");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var code = body.RootElement.GetProperty("error").GetProperty("code").GetString();
        await Assert.That(code).IsEqualTo("misdirected_request");

        using var reference = await client.GetAsync(Uri("/openapi/v1.json"));
        await Assert.That(reference.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await reference.Content.ReadAsStringAsync());
        var responses = document.RootElement.GetProperty("paths").GetProperty("/readyz").GetProperty("get").GetProperty("responses");
        var misdirected = responses.GetProperty("421");
        await Assert.That(misdirected.GetProperty("description").GetString()).Contains(code!);
        var error = misdirected.GetProperty("content").GetProperty("application/json");
        var schema = error.GetProperty("schema");
        await Assert.That(schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())).Contains("error");
        var details = schema.GetProperty("properties").GetProperty("error");
        await Assert.That(details.GetProperty("required").EnumerateArray().Select(item => item.GetString())).Contains("code");
        await Assert.That(details.GetProperty("properties").GetProperty("code").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(error.GetProperty("example").GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(error.GetProperty("example").TryGetProperty("status", out _)).IsFalse();
        var unavailable = responses.GetProperty("503").GetProperty("content").GetProperty("application/json");
        await Assert.That(unavailable.GetProperty("schema").GetRawText()).Contains("StatusBody");
        await Assert.That(unavailable.GetProperty("example").GetProperty("status").GetString()).IsEqualTo("unavailable");
        await Assert.That(unavailable.GetProperty("example").TryGetProperty("error", out _)).IsFalse();
    }

    [Test]
    public async Task Patch_conflict_examples_and_descriptions_match_project_JWT_responses()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { n = 1 })).Status).IsEqualTo(HttpStatusCode.Created);
        using var owner = api.Management.As(api.Tenant.User);
        await api.Management.VerifyProjectBindingAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, owner);
        using var client = api.Management.WithToken(api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = "documents:write";
        }));
        var ops = new object[] { new { op = "append", path = "/n", value = 1 } };
        var patch = await DocumentApi.SendAsync(client, HttpMethod.Patch, api.Document(id),
            JsonContent.Create(new { ops }, mediaType: new MediaTypeHeaderValue("application/vnd.terrabase.ops+json")));
        var transaction = await DocumentApi.SendAsync(client, HttpMethod.Post, api.Transactions,
            JsonContent.Create(new { ops = new object[] { new { op = "patch", collection = "items", id, ops } } }));
        using var reference = await client.GetAsync(Uri("/openapi/v1.json"));
        await Assert.That(reference.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await reference.Content.ReadAsStringAsync());
        var operations = json.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Value.TryGetProperty("responses", out _))
                .Select(operation => operation.Value));

        foreach (var (operationId, result) in new[] { ("patchDocument", patch), ("runTransaction", transaction) })
        {
            await Assert.That((result.Status, result.Code)).IsEqualTo((HttpStatusCode.Conflict, "patch_conflict"));
            var documented = operations.Single(operation => operation.GetProperty("operationId").GetString() == operationId)
                .GetProperty("responses").GetProperty("409");
            await Assert.That(documented.GetProperty("content").GetProperty("application/json").GetProperty("example")
                .GetProperty("error").GetProperty("code").GetString()).IsEqualTo(result.Code).Because(operationId);
            await Assert.That(documented.GetProperty("description").GetString()).Contains(result.Code!).Because(operationId);
        }
    }

    private static string Security(JsonElement operation) =>
        operation.TryGetProperty("security", out var security)
            ? string.Join(' ', security.EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()).Select(scheme =>
                $"{scheme.Name}({string.Join(',', operation.GetProperty("x-required-scopes").EnumerateArray().Select(scope => scope.GetString()))})"))
            : "anonymous";

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
