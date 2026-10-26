using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Tenancy;

/// <summary>The organization is the tenant: nothing of one organization is reachable from another.</summary>
public sealed class TenantIsolationTests
{
    // Client routes that address no organization, project or database: they act only for the caller.
    private static readonly string[] ReadScope = ["documents:read"];

    // Index fields the Management API tooling routes are sent with.
    private static readonly string[] NameField = ["name"];
    private static readonly string[] OwnerField = ["owner"];

    // The id of every document these tests write; documents are created with ULID keys.
    private const string DocumentId = "01K6G00000E008000000000001";

    private static readonly string[] CallerOnlyRoutes =
    [
        // Health probes, the region catalog and the API reference: no tenant data at all.
        "GET /",
        "GET /backupz",
        "GET /health/replication",
        "GET /healthz",
        "GET /openapi/{documentName}.json",
        "GET /readyz",
        "GET /v1/regions",
        "GET /v1/management/current-organization",
        "GET /v1/management/organizations",
        "POST /v1/management/invitations/accept",
        "POST /v1/management/organizations",
        "PUT /v1/management/current-organization",
        // Commits name organizations in their operations, each authorized like its REST route (CommitApiTests);
        // commit records and versions are the acting user's own.
        "POST /v1/management/commits",
        "GET /v1/management/commits/{id}",
        "GET /v1/management/versions",
        // A token hash names one invitation, as the accept route's token does.
        "POST /v1/management/invitations/lookup",
        // The dashboard document space belongs to the dashboard, not to a tenant; API keys never reach it.
        "GET /v1/management/documents/{collection}/by-key",
        "POST /v1/management/documents/{collection}/query",
        "GET /management/query-shapes",
        // The platform explore API: platform admins only, over TerraScale's own databases, never a tenant's (PlatformExploreTests).
        "GET /v1/platform/databases",
        "GET /v1/platform/databases/{database}/collections",
        "GET /v1/platform/databases/{database}/collections/{collection}/documents",
        "GET /v1/platform/databases/{database}/collections/{collection}/documents/by-key",
    ];

    [Test]
    public async Task A_tenant_reaches_nothing_of_another_tenant_through_any_client_route()
    {
        await using var api = await ManagementApi.StartAsync();
        // Every client scope, so each route answers past its scope check.
        using var a = await api.CreateTenantAsync("documents:read", "documents:write");
        using var b = await api.CreateTenantAsync("documents:read", "documents:write");
        using var aDashboard = api.As(a.User);
        using var bDashboard = api.As(b.User);
        await api.VerifyProjectBindingAsync(a.OrganizationId, a.ProjectId, aDashboard);
        await api.VerifyProjectBindingAsync(b.OrganizationId, b.ProjectId, bDashboard);
        HttpClient ProjectClient(Tenant tenant)
        {
            return api.WithToken(api.Issuer.Mint(tenant.User, token =>
            {
                token.Claims["project_id"] = tenant.ProjectId;
                token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
            }));
        }

        using var aProject = ProjectClient(a);
        using var bProject = ProjectClient(b);
        // Both have a database "secret" with a document, a pending invitation and an ownership transfer: everything a route can name.
        var aSecret = await SeedAsync(api, a, aDashboard);
        var bSecret = await SeedAsync(api, b, bDashboard);
        // Canonical management addresses database names: this name exists only in B's project.
        var bForeign = await CreateDatabaseAsync(b, "foreign");
        var bInvitation = await InviteAsync(b, bDashboard);
        // And the database tooling of the Management API: a project API key, a saved query, an index, a backup and a recommendation.
        var aTooling = await SeedToolingAsync(a, aDashboard, aSecret);
        var bTooling = await SeedToolingAsync(b, bDashboard, bSecret);
        // Prove EXPLAIN accepts these credentials before testing the same route against another project.
        foreach (var (tenant, client) in new[] { (a, aProject), (b, bProject) })
        {
            using var response = await client.PostAsJsonAsync(
                new Uri($"/v1/projects/{tenant.ProjectId}/databases/secret/sql/explain", UriKind.Relative), new { sql = "SELECT * FROM people" });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();
        }

        var aBefore = await SnapshotAsync(api, a, aDashboard, aSecret);
        var bBefore = await SnapshotAsync(api, b, bDashboard, bSecret);

        var routes = ClientRoutes(api.Server);
        // Scalar's page and its embedded assets are static: no tenant data at all.
        foreach (var (method, pattern, _) in routes.Where(route => !Addresses(route.Pattern) && !route.Pattern.StartsWith("/scalar", StringComparison.Ordinal)))
        {
            await Assert.That(CallerOnlyRoutes).Contains($"{method} {pattern}");
        }

        var addressing = routes.Where(route => Addresses(route.Pattern)).ToList();
        var management = addressing.Where(route => route.ProjectManagement || IsManagement(route.Pattern)).ToList();
        var foreignDatabases = management.Where(route => route.Pattern.Contains("{database}", StringComparison.Ordinal)).ToList();
        await Assert.That(foreignDatabases).IsNotEmpty();
        var withProject = addressing.Where(route => route.Pattern.Contains("{project}", StringComparison.Ordinal)).ToList();
        List<(string Request, HttpStatusCode Status)> answers =
        [
            // A's credentials (its API key, or its member on the dashboard) with B's organization and B's project.
            .. await SendAsync(addressing, aDashboard, a.Data, aProject, b.OrganizationId, b.ProjectId, bSecret, b.User, bInvitation, bTooling),
            // A's organization, where A is an owner, with B's project.
            .. await SendAsync(management.Where(route => route.Pattern.Contains("{project}", StringComparison.Ordinal)),
                aDashboard, a.Data, aProject, a.OrganizationId, b.ProjectId, bSecret, b.User, bInvitation, bTooling),
            // B's organization with A's project: A's member on B's organization, and B's key (whose organization is B) on A's project.
            .. await SendAsync(withProject, aDashboard, b.Data, bProject, b.OrganizationId, a.ProjectId, aSecret, b.User, bInvitation, aTooling),
            // A's organization and project with B's database.
            .. await SendAsync(foreignDatabases,
                aDashboard, a.Data, aProject, a.OrganizationId, a.ProjectId, bForeign, b.User, bInvitation, bTooling, databaseName: "foreign"),
        ];

        // Routes that address no tenant show A only its own organization.
        using var listed = JsonDocument.Parse(await aDashboard.GetStringAsync(new Uri("/v1/management/organizations", UriKind.Relative)));
        using var switched = await aDashboard.PutAsJsonAsync(new Uri("/v1/management/current-organization", UriKind.Relative), new { organizationId = b.OrganizationId });

        // Five data routes and twenty-eight management routes today.
        await Assert.That(addressing.Count).IsGreaterThanOrEqualTo(33);
        // Per request: every one answers 404, so another tenant's resources look exactly like ones that do not exist.
        await Assert.That(string.Join("\n", answers.Select(answer => $"{answer.Request} -> {(int)answer.Status}")))
            .IsEqualTo(string.Join("\n", answers.Select(answer => $"{answer.Request} -> 404")));
        await Assert.That(listed.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!))
            .IsEquivalentTo([a.OrganizationId]);
        await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await SnapshotAsync(api, a, aDashboard, aSecret)).IsEquivalentTo(aBefore);
        await Assert.That(await SnapshotAsync(api, b, bDashboard, bSecret)).IsEquivalentTo(bBefore);
    }

    [Test]
    public async Task Two_organizations_each_have_their_own_database_of_the_same_name()
    {
        await using var api = await ManagementApi.StartAsync();
        // Every client scope, so each route answers past its scope check.
        using var a = await api.CreateTenantAsync("documents:read", "documents:write");
        using var b = await api.CreateTenantAsync("documents:read", "documents:write");
        var document = $"/collections/people/documents/by-key?id={DocumentId}";

        var aApp = await CreateDatabaseAsync(a, "app");
        var bApp = await CreateDatabaseAsync(b, "app");
        (await a.Data.PutAsJsonAsync(a.Database("app", document), new { owner = "a" })).EnsureSuccessStatusCode();
        using var bWrite = await b.Data.PutAsJsonAsync(b.Database("app", document), new { owner = "b" });
        var aRead = await a.Data.GetFromJsonAsync<JsonElement>(a.Database("app", document));
        (await a.Data.DeleteAsync(a.Database("app", document))).EnsureSuccessStatusCode();
        var bRead = await b.Data.GetFromJsonAsync<JsonElement>(b.Database("app", document));
        using var aGone = await a.Data.GetAsync(a.Database("app", document));
        // A's key with B's project, for a name A also has.
        using var crossProject = await a.Data.GetAsync(b.Database("app", document));

        await Assert.That(aApp).IsNotEqualTo(bApp);
        // B's write created its document: A's earlier write to the same name never touched B's database.
        await Assert.That(bWrite.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(aRead.GetProperty("document").GetProperty("owner").GetString()).IsEqualTo("a");
        await Assert.That(bRead.GetProperty("document").GetProperty("owner").GetString()).IsEqualTo("b");
        await Assert.That(aGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(crossProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, a.OrganizationId, a.ProjectId, aApp + ".db"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, b.OrganizationId, b.ProjectId, bApp + ".db"))).IsTrue();
    }

    [Test]
    public async Task Keys_reach_only_their_organization_and_the_project_and_database_they_are_narrowed_to()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        using var other = await api.CreateTenantAsync("documents:read");
        using var owner = api.As(tenant.User);
        var second = await api.CreateProjectAsync(tenant.OrganizationId, "Second", owner);
        var app = await CreateDatabaseAsync(tenant, "app");
        await CreateDatabaseAsync(tenant, "other");
        using var secondApp = await tenant.Data.PutAsync(Database(second, "app"), null);
        var secondAppId = (await secondApp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var project = api.DataClient(await api.Server.IssueNarrowedAsync(tenant.OrganizationId, tenant.ProjectId, null, "documents:read", "documents:write"));
        using var database = api.DataClient(await api.Server.IssueNarrowedAsync(tenant.OrganizationId, tenant.ProjectId, app, "documents:read", "documents:write"));
        var body = new { value = 1 };
        var document = $"/collections/c/documents/by-key?id={DocumentId}";

        List<(string Request, HttpStatusCode Expected, HttpStatusCode Actual)> checks =
        [
            ("project key writes app", HttpStatusCode.Created, await StatusAsync(project.PutAsJsonAsync(tenant.Database("app", document), body))),
            ("project key writes other", HttpStatusCode.Created, await StatusAsync(project.PutAsJsonAsync(tenant.Database("other", document), body))),
            ("project key writes app of second project", HttpStatusCode.NotFound, await StatusAsync(project.PutAsJsonAsync(Database(second, "app", document), body))),
            ("project key lists app of second project", HttpStatusCode.NotFound, await StatusAsync(project.GetAsync(Database(second, "app", "/collections/c/documents")))),
            ("project key creates in second project", HttpStatusCode.NotFound, await StatusAsync(project.PutAsync(Database(second, "new"), null))),
            ("project key creates in its project", HttpStatusCode.Created, await StatusAsync(project.PutAsync(tenant.Database("new"), null))),
            ("database key writes app", HttpStatusCode.OK, await StatusAsync(database.PutAsJsonAsync(tenant.Database("app", document), body))),
            ("database key reads other", HttpStatusCode.NotFound, await StatusAsync(database.GetAsync(tenant.Database("other", document)))),
            ("database key creates app again", HttpStatusCode.OK, await StatusAsync(database.PutAsync(tenant.Database("app"), null))),
            ("database key creates unwanted", HttpStatusCode.NotFound, await StatusAsync(database.PutAsync(tenant.Database("unwanted"), null))),
            ("database key reads app of second project", HttpStatusCode.NotFound, await StatusAsync(database.GetAsync(Database(second, "app", document)))),
        ];
        using var secondDatabases = JsonDocument.Parse(await owner.GetStringAsync(
            new Uri($"/v1/management/organizations/{tenant.OrganizationId}/projects/{second}/databases", UriKind.Relative)));
        using var firstDatabases = JsonDocument.Parse(await owner.GetStringAsync(
            new Uri($"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/databases", UriKind.Relative)));

        // Per request: each status is checked against the request that produced it.
        await Assert.That(string.Join("\n", checks.Select(check => $"{check.Request} -> {check.Actual}")))
            .IsEqualTo(string.Join("\n", checks.Select(check => $"{check.Request} -> {check.Expected}")));
        // Refused creations created nothing.
        await Assert.That(secondDatabases.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["app"]);
        await Assert.That(firstDatabases.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["app", "other", "new"]);

        // Issuing: the organization is required, and a project and database must belong to it and nest.
        var issued = new List<HttpStatusCode>();
        foreach (var request in new object[]
        {
            new { name = "k", scopes = ReadScope },
            new { name = "k", scopes = ReadScope, organizationId = EntityIds.New().ToString() },
            new { name = "k", scopes = ReadScope, organizationId = tenant.OrganizationId, projectId = other.ProjectId },
            new { name = "k", scopes = ReadScope, organizationId = tenant.OrganizationId, databaseId = app },
            new { name = "k", scopes = ReadScope, organizationId = tenant.OrganizationId, projectId = tenant.ProjectId, databaseId = secondAppId },
        })
        {
            using var response = await api.Server.Client.PostAsJsonAsync(new Uri("/admin/api-keys", UriKind.Relative), request);
            issued.Add(response.StatusCode);
            await ErrorAssert.ErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request",
                "organizationId must name an organization, projectId one of its projects and databaseId (only with projectId) one of that project's databases");
        }

        using var narrowed = await api.Server.Client.PostAsJsonAsync(
            new Uri("/admin/api-keys", UriKind.Relative),
            new { name = "k", scopes = ReadScope, organizationId = tenant.OrganizationId, projectId = second, databaseId = secondAppId });
        var narrowedBody = await narrowed.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(issued.Distinct()).IsEquivalentTo([HttpStatusCode.BadRequest]);
        await Assert.That(narrowed.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(narrowedBody.GetProperty("organizationId").GetString()).IsEqualTo(tenant.OrganizationId);
        await Assert.That(narrowedBody.GetProperty("projectId").GetString()).IsEqualTo(second);
        await Assert.That(narrowedBody.GetProperty("databaseId").GetString()).IsEqualTo(secondAppId);
    }

    [Test]
    public async Task A_key_stored_before_tenancy_no_longer_authenticates()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read");
        const string secret = "tb_legacy-key-from-before-tenancy";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        // Exactly what the first API key store wrote: no organization.
        await DocumentFiles.WriteAsync(
            Path.Combine(api.Server.DataDirectory, "_system.db"), ("api_keys", hash, """{"id":"legacy","name":"old","scopes":["documents:read"]}"""));
        using var legacy = api.DataClient(secret);

        using var response = await legacy.GetAsync(tenant.Database("app", "/collections/c/documents"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static bool Addresses(string pattern) =>
        pattern.Contains("{organization}", StringComparison.Ordinal) || pattern.Contains("{project}", StringComparison.Ordinal);

    private static bool IsManagement(string pattern) =>
        pattern.StartsWith("/v1/management/", StringComparison.Ordinal) ||
        pattern.StartsWith("/management/", StringComparison.Ordinal);

    // Project management takes a project JWT, hidden identity routes a dashboard token, and legacy data an API key.
    private static async Task<List<(string Request, HttpStatusCode Status)>> SendAsync(
        IEnumerable<(string Method, string Pattern, bool ProjectManagement)> routes, HttpClient dashboard, HttpClient data, HttpClient projectToken,
        string organization, string project, string database, string user, string invitation, Tooling tooling, string databaseName = "secret")
    {
        var sent = new List<(string Request, HttpStatusCode Status)>();
        foreach (var (method, pattern, projectManagement) in routes)
        {
            var path = pattern
                .Replace("{organization}", organization, StringComparison.Ordinal)
                .Replace("{project}", project, StringComparison.Ordinal)
                .Replace("{expectedId}", database, StringComparison.Ordinal)
                // Legacy management routes name a database by id; canonical management and data use its name.
                .Replace("{database}", IsManagement(pattern) ? database : databaseName, StringComparison.Ordinal)
                .Replace("{collection}", "people", StringComparison.Ordinal)
                .Replace("{id}", DocumentId, StringComparison.Ordinal)
                .Replace("{user}", user, StringComparison.Ordinal)
                .Replace("{invitation}", invitation, StringComparison.Ordinal)
                .Replace("{key}", tooling.Key, StringComparison.Ordinal)
                .Replace("{query}", tooling.Query, StringComparison.Ordinal)
                .Replace("{backup}", tooling.Backup, StringComparison.Ordinal)
                .Replace("{webhook}", DocumentId, StringComparison.Ordinal)
                .Replace("{function}", "hello", StringComparison.Ordinal)
                .Replace("{bucket}", DocumentId, StringComparison.Ordinal)
                .Replace("{file}", DocumentId, StringComparison.Ordinal)
                .Replace("{index}", tooling.Index, StringComparison.Ordinal)
                .Replace("{recommendation}", tooling.Recommendation, StringComparison.Ordinal)
                .Replace("{version}", "v3", StringComparison.Ordinal);
            // A new route parameter needs a value here.
            await Assert.That(path).DoesNotContain("{");
            if (pattern.EndsWith("/documents/by-key", StringComparison.Ordinal))
            {
                path += "?id=" + System.Uri.EscapeDataString(DocumentId);
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
            if (method is "POST" or "PUT" or "PATCH")
            {
                // Valid for every route, so only tenancy can refuse it.
                request.Content = JsonContent.Create(new
                {
                    organization,
                    name = "intruder",
                    plan = "enterprise",
                    email = "intruder@example.com",
                    role = "member",
                    userId = user,
                    days = 30,
                    scopes = ReadScope,
                    sql = "SELECT * FROM people",
                    collection = "people",
                    fields = OwnerField,
                    fullBackupCron = "",
                    retentionDays = 7,
                });
            }

            var client = projectManagement ? projectToken : IsManagement(pattern) ? dashboard : data;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            sent.Add(($"{method} {path}", response.StatusCode));
        }

        return sent;
    }

    // A database "secret" holding document p1, a pending invitation and an ownership transfer; returns the database id.
    private static async Task<string> SeedAsync(ManagementApi api, Tenant tenant, HttpClient dashboard)
    {
        var id = await CreateDatabaseAsync(tenant, "secret");
        (await tenant.Data.PutAsJsonAsync(tenant.Database("secret", $"/collections/people/documents/by-key?id={DocumentId}"), new { owner = tenant.OrganizationId }))
            .EnsureSuccessStatusCode();
        await InviteAsync(tenant, dashboard);
        var nominee = await api.AddMemberAsync(dashboard, tenant.OrganizationId, "member");
        var transfer = new Uri($"/v1/management/organizations/{tenant.OrganizationId}/ownership-transfer", UriKind.Relative);
        using var started = await dashboard.PostAsJsonAsync(transfer, new { userId = nominee });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var pending = await dashboard.GetAsync(transfer);
        await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("toUserId").GetString()).IsEqualTo(nominee);
        return id;
    }

    // The Management API's tooling of a database: a project API key, a saved query, an index on people.name, a backup, and a
    // recommendation for people.owner from a SQL run.
    private static async Task<Tooling> SeedToolingAsync(Tenant tenant, HttpClient dashboard, string database)
    {
        var project = $"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}";
        var path = $"{project}/databases/{database}";
        async Task<JsonElement> PostAsync(string route, object? body)
        {
            using var response = await dashboard.PostAsJsonAsync(new Uri(route, UriKind.Relative), body);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var key = (await PostAsync($"{project}/api-keys", new { name = "ci", scopes = ReadScope })).GetProperty("key").GetProperty("id").GetString()!;
        var query = (await PostAsync($"{project}/saved-queries", new { name = "All", sql = "SELECT * FROM people" })).GetProperty("id").GetString()!;
        var backup = (await PostAsync($"{path}/backups", null)).GetProperty("id").GetString()!;
        var index = (await PostAsync($"{path}/indexes", new { collection = "people", fields = NameField })).GetProperty("name").GetString()!;
        await PostAsync($"{path}/sql", new { sql = "SELECT * FROM people WHERE owner = 'nobody'" });
        return new Tooling(key, query, backup, index, "people:owner");
    }

    // A pending invitation; returns its id.
    private static async Task<string> InviteAsync(Tenant tenant, HttpClient dashboard)
    {
        using var invited = await dashboard.PostAsJsonAsync(
            new Uri($"/v1/management/organizations/{tenant.OrganizationId}/invitations", UriKind.Relative),
            new { email = $"{EntityIds.New()}@example.com", role = "member" });
        invited.EnsureSuccessStatusCode();
        return (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitation").GetProperty("id").GetString()!;
    }

    private static Uri Database(string project, string name, string path = "") => new($"/v1/projects/{project}/databases/{name}{path}", UriKind.Relative);

    // A database with the collections the tests write to, "people" and "c"; returns its id.
    private static async Task<string> CreateDatabaseAsync(Tenant tenant, string name)
    {
        using var response = await tenant.Data.PutAsync(tenant.Database(name), null);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        foreach (var collection in new[] { "people", "c" })
        {
            using var created = await tenant.Data.PostAsJsonAsync(tenant.Database(name, "/collections"), new { name = collection });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    // Every method and route pattern the client listener serves.
    private static List<(string Method, string Pattern, bool ProjectManagement)> ClientRoutes(TerraBaseTestServer server) =>
    [
        .. server.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ListenerMetadata>()?.Listeners.Contains(TerraBaseListener.Client) == true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (method, endpoint.RoutePattern.RawText!, endpoint.Metadata.GetMetadata<ProjectManagementRoute>() is not null
                    || endpoint.RoutePattern.RawText!.EndsWith("/sql/explain", StringComparison.Ordinal))))
            .Order(),
    ];

    // Everything of a tenant that another could have read, changed or deleted: its organization, members, invitations,
    // ownership transfer, audit log, projects, databases, documents and files.
    private static async Task<List<string>> SnapshotAsync(ManagementApi api, Tenant b, HttpClient bDashboard, string secret)
    {
        var organization = $"/v1/management/organizations/{b.OrganizationId}";
        var project = $"{organization}/projects/{b.ProjectId}";
        var database = $"{project}/databases/{secret}";
        var snapshot = new List<string>();
        foreach (var path in new[]
        {
            organization, $"{organization}/projects", project, $"{project}/databases", $"{organization}/members", $"{organization}/invitations",
            $"{organization}/ownership-transfer", $"{organization}/audit-retention", $"{organization}/audit-events",
            $"{project}/api-keys", $"{project}/saved-queries", $"{database}/indexes", $"{database}/backups", $"{database}/backup-policy",
            $"{database}/index-recommendations",
        })
        {
            using var response = await bDashboard.GetAsync(new Uri(path, UriKind.Relative));
            snapshot.Add($"{path} {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        snapshot.Add(await b.Data.GetStringAsync(b.Database("secret", "/collections/people/documents")));
        snapshot.AddRange(Directory.EnumerateFiles(Path.Combine(api.Server.DataDirectory, b.OrganizationId), "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".db", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
        return snapshot;
    }

    /// <summary>The ids of a tenant's Management API tooling that routes name.</summary>
    private sealed record Tooling(string Key, string Query, string Backup, string Index, string Recommendation);
}
