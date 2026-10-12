using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class DatabaseRegistrationJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments("organization-missing", 404)]
    [Arguments("project-missing", 404)]
    [Arguments("project-unauthenticated", 401)]
    [Arguments("project-forbidden", 403)]
    [Arguments("project-unavailable", 503)]
    [Arguments("organization-identity", 503)]
    [Arguments("project-identity", 503)]
    [Arguments("project-organization", 503)]
    public async Task UnavailableRegionalScopeNeverUsesOperatorCommitsOrInventsResources(string fault, int status)
    {
        var armed = false;
        var organizationId = "";
        var projectId = "";
        var userId = "";
        var refusedReads = 0;
        var forbiddenCommits = 0;
        var unauthorizedCreates = 0;
        const string targetHost = "gru.api.terrascale.tech";
        string[] regions = ["GRU"];
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method == "POST" && http.Request.Path == "/v1/management/commits")
            {
                Interlocked.Increment(ref forbiddenCommits);
                // The actual producer rejects a user's chosen project ID.
                // This is a trap, never a permissive registration substitute.
                return Task.FromResult<IResult?>(Failure(403, "forbidden"));
            }
            if (!Volatile.Read(ref armed)) return Task.FromResult<IResult?>(null);
            var organizationPath = "/v1/management/organizations/" + organizationId;
            var projectPath = organizationPath + "/projects/" + projectId;
            if (http.Request.Method == "POST" && (http.Request.Path == "/v1/management/organizations" ||
                http.Request.Path == organizationPath + "/projects"))
            {
                Interlocked.Increment(ref unauthorizedCreates);
                return Task.FromResult<IResult?>(Failure(403, "forbidden"));
            }
            var expectedPath = fault.StartsWith("organization-", StringComparison.Ordinal) ? organizationPath : projectPath;
            if (http.Request.Method != "GET" || http.Request.Host.Host != targetHost || http.Request.Path != expectedPath)
                return Task.FromResult<IResult?>(null);
            if (http.Request.Headers["X-TerraBase-Organization-Id"] != organizationId ||
                http.Request.Headers["X-TerraBase-User-Id"] != userId ||
                !http.Request.Headers.Authorization.ToString().StartsWith("Bearer test-access-", StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog faults must use the ordinary admitted user's exact canonical organization and acting subject.");
            Interlocked.Increment(ref refusedReads);
            if (fault == "organization-identity")
                return Task.FromResult<IResult?>(Results.Json(new
                { id = "01K78000000000000000000000", name = "Foreign organization", createdBy = userId, createdAt = DateTimeOffset.UtcNow, plan = "hobby" }));
            if (fault is "project-identity" or "project-organization")
                return Task.FromResult<IResult?>(Results.Json(new
                {
                    id = fault == "project-identity" ? "01K78000000000000000000001" : projectId,
                    organizationId = fault == "project-organization" ? "01K78000000000000000000000" : organizationId,
                    name = "Customer SDK project",
                    createdAt = DateTimeOffset.UtcNow
                }));
            var code = status switch { 401 => "unauthorized", 403 => "forbidden", 503 => "unavailable", _ => "not_found" };
            return Task.FromResult<IResult?>(Failure(status, code));
        });
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        organizationId = identity.GetProperty("organizationId").GetString()!;
        projectId = identity.GetProperty("projectId").GetString()!;
        userId = identity.GetProperty("userId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        var secret = await HttpJourney.CreatePatAsync(client, "Unavailable regional catalog", account.Password);
        var baseline = await ReadAsync(producer, "/__test/observations");
        var baselineProjects = ProjectIdentities(baseline);
        const string name = "catalog-recovery";
        Volatile.Write(ref armed, true);
        using (var refused = await CreateAsync(client, csrf, name, regions))
        {
            await Assert.That((int)refused.StatusCode).IsEqualTo(status).Because(await EvidenceAsync(refused, producer));
            await Assert.That(refused.Headers.Location is null).IsTrue();
        }
        var rpc = await HttpJourney.McpAsync(replica, secret, "tools/call", new
        { name = "create_database", arguments = new { organizationId, projectId, name, regions } });
        await Assert.That(rpc.TryGetProperty("error", out _) ||
            rpc.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean()).IsTrue().Because(rpc.GetRawText());
        await Assert.That(refusedReads).IsEqualTo(2).Because("Both native and MCP creation reach the genuine scoped catalog boundary.");
        await Assert.That(forbiddenCommits).IsEqualTo(0);
        await Assert.That(unauthorizedCreates).IsEqualTo(0);
        var observed = await ReadAsync(producer, "/__test/observations");
        await Assert.That(observed.GetProperty("creates").GetInt32()).IsEqualTo(0);
        await Assert.That(observed.GetProperty("projectCreates").GetInt32()).IsEqualTo(baseline.GetProperty("projectCreates").GetInt32());
        await Assert.That(ProjectIdentities(observed)).IsEqualTo(baselineProjects);
        await Assert.That(observed.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
        await Assert.That((await ReadAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
        await Assert.That((await ReadAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);

        // Once the catalog genuinely resolves the existing scope, the same
        // ordinary account can proceed without creating another project.
        Volatile.Write(ref armed, false);
        string databaseId;
        using (var created = await CreateAsync(client, csrf, name, regions))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(created, producer));
            databaseId = (await BodyAsync(created)).GetProperty("id").GetString()!;
        }
        var canonical = (await ReadAsync(replica, "/api/v1/dashboard/databases"))[0];
        await Assert.That(canonical.GetProperty("id").GetString()).IsEqualTo(databaseId);
        await Assert.That(canonical.GetProperty("projectId").GetString()).IsEqualTo(projectId);
        await Assert.That(string.Join(',', canonical.GetProperty("regions").EnumerateArray().Select(value => value.GetString()))).IsEqualTo(string.Join(',', regions));
        if (fault == "project-missing")
        {
            using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = "users" }))
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(collection, producer));
            string keyId; string customerSecret;
            using (var credential = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new()
            { ["_csrf"] = csrf, ["name"] = "Catalog recovery SDK", ["databaseId"] = databaseId, ["scopes"] = "database:read,database:write" }))
            {
                await Assert.That(credential.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(credential, producer));
                var issued = await BodyAsync(credential);
                keyId = issued.GetProperty("apiKey").GetProperty("keyId").GetString()!;
                customerSecret = issued.GetProperty("secret").GetString()!;
            }
            await DatabaseSdkJourneyTests.RunCustomerSdkAsync(fixture.Root, scenario, producer, projectId, name, customerSecret);
            using var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/" + keyId + "/revoke", new() { ["_csrf"] = csrf });
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await EvidenceAsync(revoked, producer));
        }
        using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = name }))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await EvidenceAsync(deleted, producer));
        observed = await ReadAsync(producer, "/__test/observations");
        await Assert.That(ProjectIdentities(observed)).IsEqualTo(baselineProjects);
        await Assert.That(observed.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
        await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        await Assert.That(forbiddenCommits).IsEqualTo(0);
    }

    private static IResult Failure(int status, string code) => Results.Json(new
    { error = new { code, message = "Scoped test-only catalog dependency failure", fix = "retry" } }, statusCode: status);
    private static string ProjectIdentities(JsonElement observations) => string.Join('|', observations.GetProperty("workspaceProjects").EnumerateArray()
        .Select(value => value.GetProperty("organizationId").GetString() + "/" + value.GetProperty("id").GetString() + "/" + value.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, string csrf, string name, string[] regions)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/databases");
        request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("_csrf", csrf), new("name", name) }
            .Concat(regions.Select(region => new KeyValuePair<string, string>("region", region))));
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    { using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return value.RootElement.Clone(); }
    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return await BodyAsync(response); }
    private static async Task<string> EvidenceAsync(HttpResponseMessage response, HttpClient producer)
    {
        var body = await response.Content.ReadAsStringAsync();
        var observed = await ReadAsync(producer, "/__test/observations");
        return "Native response: " + body[..Math.Min(body.Length, 1200)] + "; producer failures: " + observed.GetProperty("failures").GetRawText();
    }
}
