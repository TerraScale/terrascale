using System.Net;
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

    // The id route parameters are filled with.
    private const string Id = "01K6G00000E008000000000001";

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
        "GET /v1/management/organizations",
        "POST /v1/management/organizations",
        // Platform admins only, over TerraScale's own databases, never a tenant's.
        "GET /v1/platform/databases",
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
        var aSecret = await SeedAsync(api, a, aDashboard);
        var bSecret = await SeedAsync(api, b, bDashboard);
        // Canonical management addresses database names: this name exists only in B's project.
        var bForeign = await CreateDatabaseAsync(api, b, bDashboard, "foreign");
        var aTooling = await SeedToolingAsync(a, aDashboard, aSecret);
        var bTooling = await SeedToolingAsync(b, bDashboard, bSecret);

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
            .. await SendAsync(addressing, aDashboard, a.Data, aProject, b.OrganizationId, b.ProjectId, bSecret, b.User, bTooling),
            // A's organization, where A is an owner, with B's project.
            .. await SendAsync(management.Where(route => route.Pattern.Contains("{project}", StringComparison.Ordinal)),
                aDashboard, a.Data, aProject, a.OrganizationId, b.ProjectId, bSecret, b.User, bTooling),
            .. await SendAsync(withProject, aDashboard, b.Data, bProject, b.OrganizationId, a.ProjectId, aSecret, b.User, aTooling),
            // A's organization and project with B's database.
            .. await SendAsync(foreignDatabases,
                aDashboard, a.Data, aProject, a.OrganizationId, a.ProjectId, bForeign, b.User, bTooling, databaseName: "foreign"),
        ];

        // Routes that address no tenant show A only its own organization.
        using var listed = JsonDocument.Parse(await aDashboard.GetStringAsync(new Uri("/v1/management/organizations", UriKind.Relative)));

        await Assert.That(addressing.Count).IsGreaterThanOrEqualTo(20);
        // Per request: every one answers 404, so another tenant's resources look exactly like ones that do not exist.
        await Assert.That(string.Join("\n", answers.Select(answer => $"{answer.Request} -> {(int)answer.Status}")))
            .IsEqualTo(string.Join("\n", answers.Select(answer => $"{answer.Request} -> 404")));
        await Assert.That(listed.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!))
            .IsEquivalentTo([a.OrganizationId]);
        await Assert.That(await SnapshotAsync(api, a, aDashboard, aSecret)).IsEquivalentTo(aBefore);
        await Assert.That(await SnapshotAsync(api, b, bDashboard, bSecret)).IsEquivalentTo(bBefore);
    }

    [Test]
    public async Task Keys_reach_only_their_organization_and_the_project_and_database_they_are_narrowed_to()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        using var other = await api.CreateTenantAsync("documents:read");
        using var owner = api.As(tenant.User);
        var second = await api.CreateProjectAsync(tenant.OrganizationId, "Second", owner);
        var app = await CreateDatabaseAsync(api, tenant, owner, "app");
        await CreateDatabaseAsync(api, tenant, owner, "other");
        var secondAppId = await api.CreateDatabaseAsync(tenant.OrganizationId, second, "app", owner);
        using var project = api.DataClient(await api.Server.IssueNarrowedAsync(tenant.OrganizationId, tenant.ProjectId, null, "documents:read", "documents:write"));
        using var database = api.DataClient(await api.Server.IssueNarrowedAsync(tenant.OrganizationId, tenant.ProjectId, app, "documents:read", "documents:write"));
        const string Write = "INSERT INTO c (value) VALUES (1)";

        List<(string Request, HttpStatusCode Expected, HttpStatusCode Actual)> checks =
        [
            ("project key writes app", HttpStatusCode.OK, await StatusAsync(ManagementApi.SqlAsync(project, DataPath(tenant.ProjectId, "app"), Write))),
            ("project key writes other", HttpStatusCode.OK, await StatusAsync(ManagementApi.SqlAsync(project, DataPath(tenant.ProjectId, "other"), Write))),
            ("project key writes app of second project", HttpStatusCode.NotFound, await StatusAsync(ManagementApi.SqlAsync(project, DataPath(second, "app"), Write))),
            ("project key reads app of second project", HttpStatusCode.NotFound, await StatusAsync(ManagementApi.SqlAsync(project, DataPath(second, "app"), "SELECT * FROM c"))),
            ("database key writes app", HttpStatusCode.OK, await StatusAsync(ManagementApi.SqlAsync(database, DataPath(tenant.ProjectId, "app"), Write))),
            ("database key reads other", HttpStatusCode.NotFound, await StatusAsync(ManagementApi.SqlAsync(database, DataPath(tenant.ProjectId, "other"), "SELECT * FROM c"))),
            ("database key reads app of second project", HttpStatusCode.NotFound, await StatusAsync(ManagementApi.SqlAsync(database, DataPath(second, "app"), "SELECT * FROM c"))),
        ];
        using var secondDatabases = JsonDocument.Parse(await owner.GetStringAsync(
            new Uri($"/v1/management/organizations/{tenant.OrganizationId}/projects/{second}/databases", UriKind.Relative)));
        using var firstDatabases = JsonDocument.Parse(await owner.GetStringAsync(
            new Uri($"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/databases", UriKind.Relative)));

        // Per request: each status is checked against the request that produced it.
        await Assert.That(string.Join("\n", checks.Select(check => $"{check.Request} -> {check.Actual}")))
            .IsEqualTo(string.Join("\n", checks.Select(check => $"{check.Request} -> {check.Expected}")));
        // Refused writes created nothing.
        await Assert.That(secondDatabases.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["app"]);
        await Assert.That(firstDatabases.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["app", "other"]);

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

    private static bool Addresses(string pattern) =>
        pattern.Contains("{organization}", StringComparison.Ordinal) || pattern.Contains("{project}", StringComparison.Ordinal);

    private static bool IsManagement(string pattern) =>
        pattern.StartsWith("/v1/management/", StringComparison.Ordinal) ||
        pattern.StartsWith("/management/", StringComparison.Ordinal);

    // Project management takes a project JWT, hidden identity routes a dashboard token, and legacy data an API key.
    private static async Task<List<(string Request, HttpStatusCode Status)>> SendAsync(
        IEnumerable<(string Method, string Pattern, bool ProjectManagement)> routes, HttpClient dashboard, HttpClient data, HttpClient projectToken,
        string organization, string project, string database, string user, Tooling tooling, string databaseName = "secret")
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
                .Replace("{table}", "people", StringComparison.Ordinal)
                .Replace("{id}", Id, StringComparison.Ordinal)
                .Replace("{user}", user, StringComparison.Ordinal)
                .Replace("{backup}", tooling.Backup, StringComparison.Ordinal)
                .Replace("{function}", "hello", StringComparison.Ordinal)
                .Replace("{bucket}", Id, StringComparison.Ordinal)
                .Replace("{file}", Id, StringComparison.Ordinal)
                .Replace("{version}", "v3", StringComparison.Ordinal);
            // A new route parameter needs a value here.
            await Assert.That(path).DoesNotContain("{");
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
                    statements = new[] { new { sql = "SELECT * FROM people" } },
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

    private static async Task<string> SeedAsync(ManagementApi api, Tenant tenant, HttpClient dashboard)
    {
        var id = await CreateDatabaseAsync(api, tenant, dashboard, "secret");
        (await ManagementApi.SqlAsync(tenant.Data, DataPath(tenant.ProjectId, "secret"), $"INSERT INTO people (owner) VALUES ('{tenant.OrganizationId}')"))
            .EnsureSuccessStatusCode();
        return id;
    }

    // The Management API's tooling of a database: a backup.
    private static async Task<Tooling> SeedToolingAsync(Tenant tenant, HttpClient dashboard, string database)
    {
        var path = $"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/databases/{database}";
        using var response = await dashboard.PostAsJsonAsync(new Uri($"{path}/backups", UriKind.Relative), (object?)null);
        response.EnsureSuccessStatusCode();
        return new Tooling((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    private static string DataPath(string project, string name) => $"/v1/projects/{project}/databases/{name}";

    // A database with the tables the tests write to, "people" and "c"; returns its id.
    private static async Task<string> CreateDatabaseAsync(ManagementApi api, Tenant tenant, HttpClient dashboard, string name)
    {
        var id = await api.CreateDatabaseAsync(tenant.OrganizationId, tenant.ProjectId, name, dashboard);
        foreach (var table in new[] { "CREATE TABLE people (id INTEGER PRIMARY KEY, owner TEXT)", "CREATE TABLE c (id INTEGER PRIMARY KEY, value INTEGER)" })
        {
            using var created = await ManagementApi.SqlAsync(tenant.Data, DataPath(tenant.ProjectId, name), table);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        return id;
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
                    )))
            .Order(),
    ];

    private static async Task<List<string>> SnapshotAsync(ManagementApi api, Tenant b, HttpClient bDashboard, string secret)
    {
        var organization = $"/v1/management/organizations/{b.OrganizationId}";
        var project = $"{organization}/projects/{b.ProjectId}";
        var database = $"{project}/databases/{secret}";
        var snapshot = new List<string>();
        foreach (var path in new[]
        {
            organization, $"{organization}/projects", project, $"{project}/databases",
            $"{database}/backups", $"{database}/backup-policy",
        })
        {
            using var response = await bDashboard.GetAsync(new Uri(path, UriKind.Relative));
            snapshot.Add($"{path} {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        snapshot.AddRange((await ManagementApi.RowsAsync(b.Data, DataPath(b.ProjectId, "secret"), "SELECT * FROM people")).Select(row => row.GetRawText()));
        snapshot.AddRange(Directory.EnumerateFiles(Path.Combine(api.Server.DataDirectory, b.OrganizationId), "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".db", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
        return snapshot;
    }

    /// <summary>The ids of a tenant's Management API tooling that routes name.</summary>
    private sealed record Tooling(string Backup);
}
