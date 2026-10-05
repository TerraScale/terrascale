using System.Net;
using System.Text.Json;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Platform;

public sealed class ApiReferenceTests
{
    // Every operation of the public document with its security requirement. Update it with every public endpoint;
    // an admin-listener operation appearing here is a leak.
    private const string Operations = """
        DELETE /v1/management/organizations/{organization} TsAuth
        DELETE /v1/management/organizations/{organization}/invitations/{invitation} TsAuth
        DELETE /v1/management/organizations/{organization}/members/{user} TsAuth
        DELETE /v1/management/organizations/{organization}/ownership-transfer TsAuth
        DELETE /v1/management/organizations/{organization}/projects/{project} TsAuth
        DELETE /v1/management/organizations/{organization}/projects/{project}/api-keys/{key} TsAuth
        DELETE /v1/management/organizations/{organization}/projects/{project}/databases/{database} TsAuth
        DELETE /v1/management/organizations/{organization}/projects/{project}/databases/{database}/indexes/{index} TsAuth
        DELETE /v1/management/organizations/{organization}/projects/{project}/saved-queries/{query} TsAuth
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection} ApiKey(documents:write)
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/documents/{id} ApiKey(documents:write)
        GET /healthz anonymous
        GET /readyz anonymous
        GET /v1/management/current-organization TsAuth
        GET /v1/management/organizations TsAuth
        GET /v1/management/organizations/{organization} TsAuth
        GET /v1/management/organizations/{organization}/audit-events TsAuth
        GET /v1/management/organizations/{organization}/audit-retention TsAuth
        GET /v1/management/organizations/{organization}/invitations TsAuth
        GET /v1/management/organizations/{organization}/members TsAuth
        GET /v1/management/organizations/{organization}/ownership-transfer TsAuth
        GET /v1/management/organizations/{organization}/projects TsAuth
        GET /v1/management/organizations/{organization}/projects/{project} TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/api-keys TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database} TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backup-policy TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backups TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/collections TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/collections/{collection}/schema TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/index-recommendations TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/indexes TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/usage TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/saved-queries TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/saved-queries/{query} TsAuth
        GET /v1/management/organizations/{organization}/projects/{project}/usage TsAuth
        GET /v1/projects/{project}/databases/{database}/collections ApiKey(documents:read)
        GET /v1/projects/{project}/databases/{database}/collections/{collection} ApiKey(documents:read)
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/documents ApiKey(documents:read)
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/documents/{id} ApiKey(documents:read)
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape ApiKey(shapes:read)
        PATCH /v1/management/organizations/{organization} TsAuth
        PATCH /v1/management/organizations/{organization}/projects/{project} TsAuth
        PATCH /v1/projects/{project}/databases/{database}/collections/{collection}/documents/{id} ApiKey(documents:write)
        POST /v1/management/invitations/accept TsAuth
        POST /v1/management/organizations TsAuth
        POST /v1/management/organizations/{organization}/invitations TsAuth
        POST /v1/management/organizations/{organization}/leave TsAuth
        POST /v1/management/organizations/{organization}/ownership-transfer TsAuth
        POST /v1/management/organizations/{organization}/ownership-transfer/confirm TsAuth
        POST /v1/management/organizations/{organization}/projects TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/api-keys TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backups TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backups/{backup}/restore TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/branches TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/index-recommendations/{recommendation}/apply TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/indexes TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/sql TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/sql/check TsAuth
        POST /v1/management/organizations/{organization}/projects/{project}/saved-queries TsAuth
        POST /v1/projects/{project}/databases/{database}/collections ApiKey(documents:write)
        POST /v1/projects/{project}/databases/{database}/collections/{collection}/documents ApiKey(documents:write)
        POST /v1/projects/{project}/databases/{database}/collections/{collection}/query ApiKey(documents:read)
        POST /v1/projects/{project}/databases/{database}/transactions ApiKey(documents:write)
        PUT /v1/management/current-organization TsAuth
        PUT /v1/management/organizations/{organization}/audit-retention TsAuth
        PUT /v1/management/organizations/{organization}/members/{user}/role TsAuth
        PUT /v1/management/organizations/{organization}/plan TsAuth
        PUT /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backup-policy TsAuth
        PUT /v1/management/organizations/{organization}/projects/{project}/saved-queries/{query} TsAuth
        PUT /v1/projects/{project}/databases/{database} ApiKey(documents:write)
        PUT /v1/projects/{project}/databases/{database}/collections/{collection}/documents/{id} ApiKey(documents:write)
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
                .Select(operation => (Line: $"{operation.Name.ToUpperInvariant()} {path.Name} {Security(operation.Value)}", Operation: operation.Value)))
            .ToList();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(document.GetProperty("info").GetProperty("version").GetString()).IsEqualTo(ServiceVersion.Value);
        var server0 = document.GetProperty("servers")[0];
        await Assert.That(server0.GetProperty("url").GetString()).IsEqualTo("/");
        await Assert.That(server0.GetProperty("description").GetString()).Contains("gru-1");
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        await Assert.That(schemes.GetProperty("ApiKey").GetProperty("scheme").GetString()).IsEqualTo("bearer");
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("bearerFormat").GetString()).IsEqualTo("JWT");
        await Assert.That(string.Join('\n', operations.Select(operation => operation.Line).Order(StringComparer.Ordinal)))
            .IsEqualTo(Operations.ReplaceLineEndings("\n"));
        foreach (var (line, operation) in operations)
        {
            var responses = operation.GetProperty("responses").EnumerateObject().ToList();
            // Every operation documents a success, and every error status the error envelope (or the readiness body).
            await Assert.That(responses.Any(status => status.Name.StartsWith('2'))).IsTrue().Because(line);
            foreach (var error in responses.Where(status => string.CompareOrdinal(status.Name, "400") >= 0))
            {
                var schema = error.Value.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetRawText();
                await Assert.That(schema.Contains("ErrorBody", StringComparison.Ordinal) || schema.Contains("\"error\"", StringComparison.Ordinal)
                    || (line.StartsWith("GET /readyz", StringComparison.Ordinal) && schema.Contains("StatusBody", StringComparison.Ordinal))).IsTrue().Because($"{line} {error.Name}");
            }

            if (!line.EndsWith("anonymous", StringComparison.Ordinal))
            {
                await Assert.That(responses.Any(status => status.Name == "401")).IsTrue().Because(line);
            }
        }
    }

    private static string Security(JsonElement operation) =>
        operation.TryGetProperty("security", out var security)
            ? string.Join(' ', security.EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()).Select(scheme =>
                scheme.Name + (scheme.Value.GetArrayLength() > 0 ? $"({string.Join(',', scheme.Value.EnumerateArray().Select(scope => scope.GetString()))})" : "")))
            : "anonymous";

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
