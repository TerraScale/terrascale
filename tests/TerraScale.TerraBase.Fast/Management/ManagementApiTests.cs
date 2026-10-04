using System.Net;
using System.Text.Json;
using Bogus;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

public sealed class ManagementApiTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Organizations_are_created_listed_renamed_and_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var name = Faker.Company.CompanyName();

        using var created = await api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name });
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = organization.GetProperty("id").GetString()!;
        using var read = await api.Client.GetAsync(Uri($"/v1/management/organizations/{id}"));
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations")));
        using var renamed = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{id}"), new { name = "Renamed Org" });
        var afterRename = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{id}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{id}"));
        using var gone = await api.Client.GetAsync(Uri($"/v1/management/organizations/{id}"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"/v1/management/organizations/{id}");
        await Assert.That(organization.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(Guid.TryParse(id, out _)).IsTrue();
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(list.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([id]);
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(afterRename.GetProperty("name").GetString()).IsEqualTo("Renamed Org");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Organization_lists_page_in_creation_order()
    {
        await using var api = await ManagementApi.StartAsync();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await api.CreateOrganizationAsync($"Org {i}"));
        }

        using var first = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations?limit=2")));
        var next = first.RootElement.GetProperty("next").GetString();
        using var second = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"/v1/management/organizations?limit=2&after={next}")));

        string[] seen =
        [
            .. first.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!),
            .. second.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!),
        ];
        await Assert.That(seen).IsEquivalentTo(ids);
        await Assert.That(seen[0]).IsEqualTo(ids[0]);
        await Assert.That(second.RootElement.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task A_user_switches_the_current_organization()
    {
        await using var api = await ManagementApi.StartAsync();
        var user = Faker.Random.Guid().ToString();
        var first = await api.CreateOrganizationAsync("First");
        var second = await api.CreateOrganizationAsync("Second");

        using var none = await api.Client.GetAsync(Uri($"/v1/management/users/{user}/current-organization"));
        using var switchedFirst = await api.Client.PutAsJsonAsync(Uri($"/v1/management/users/{user}/current-organization"), new { organizationId = first });
        using var switchedSecond = await api.Client.PutAsJsonAsync(Uri($"/v1/management/users/{user}/current-organization"), new { organizationId = second });
        var current = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/users/{user}/current-organization"));
        using var otherUser = await api.Client.GetAsync(Uri("/v1/management/users/someone-else/current-organization"));
        using var unknown = await api.Client.PutAsJsonAsync(Uri($"/v1/management/users/{user}/current-organization"), new { organizationId = Guid.NewGuid() });
        using var missing = await api.Client.PutAsJsonAsync(Uri($"/v1/management/users/{user}/current-organization"), new { });
        (await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{second}"))).Dispose();
        using var afterDelete = await api.Client.GetAsync(Uri($"/v1/management/users/{user}/current-organization"));

        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(switchedFirst.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(switchedSecond.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(current.GetProperty("id").GetString()).IsEqualTo(second);
        await Assert.That(otherUser.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(afterDelete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Projects_are_created_listed_renamed_and_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var other = await api.CreateOrganizationAsync("Other");
        var projects = $"/v1/management/organizations/{organization}/projects";

        using var created = await api.Client.PostAsJsonAsync(Uri(projects), new { name = "Production" });
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(projects)));
        using var renamed = await api.Client.PatchAsJsonAsync(Uri($"{projects}/{project}"), new { name = "Staging" });
        var read = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{projects}/{project}"));
        using var underOtherOrganization = await api.Client.GetAsync(Uri($"/v1/management/organizations/{other}/projects/{project}"));
        using var organizationNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{projects}/{project}"));
        using var gone = await api.Client.GetAsync(Uri($"{projects}/{project}"));
        using var organizationEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{projects}/{project}");
        await Assert.That(list.RootElement.GetProperty("items")[0].GetProperty("organizationId").GetString()).IsEqualTo(organization);
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.GetProperty("name").GetString()).IsEqualTo("Staging");
        await Assert.That(underOtherOrganization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(organizationNotEmpty.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(organizationEmpty.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Databases_are_created_branched_and_deleted_with_their_data()
    {
        using var tls = new ClientTls();
        await using var api = await ManagementApi.StartAsync(tls);
        using var data = tls.CreateClient(api.Server.ClientAddress!, await api.Server.IssueAsync("documents:read", "documents:write"));
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Shop");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        var person = new { name = Faker.Name.FullName() };

        using var created = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var main = await created.Content.ReadFromJsonAsync<JsonElement>();
        var mainId = main.GetProperty("id").GetString()!;
        var mainStorage = main.GetProperty("storageName").GetString()!;
        (await data.PutAsJsonAsync(Uri($"/v1/databases/{mainStorage}/collections/people/documents/p1"), person)).EnsureSuccessStatusCode();
        using var branched = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "preview" });
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>();
        var branchStorage = branch.GetProperty("storageName").GetString()!;
        (await data.PutAsJsonAsync(Uri($"/v1/databases/{branchStorage}/collections/people/documents/p2"), person)).EnsureSuccessStatusCode();
        using var mainWithoutBranchWrite = await data.GetAsync(Uri($"/v1/databases/{mainStorage}/collections/people/documents/p2"));
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));
        using var projectNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{databases}/{mainId}"));
        using var gone = await api.Client.GetAsync(Uri($"{databases}/{mainId}"));
        using var dataGone = await data.GetAsync(Uri($"/v1/databases/{mainStorage}/collections/people/documents/p1"));
        using var branchKeepsCopy = await data.GetAsync(Uri($"/v1/databases/{branchStorage}/collections/people/documents/p1"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(main.GetProperty("name").GetString()).IsEqualTo("main");
        await Assert.That(main.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(branch.GetProperty("branchedFrom").GetString()).IsEqualTo(mainId);
        await Assert.That(mainWithoutBranchWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(projectNotEmpty.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(dataGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, mainStorage + ".db"))).IsFalse();
        await Assert.That(branchKeepsCopy.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Duplicate_names_conflict_ignoring_case_within_the_same_parent()
    {
        await using var api = await ManagementApi.StartAsync();
        var acme = await api.CreateOrganizationAsync("Acme");
        var globex = await api.CreateOrganizationAsync("Globex");
        var project = await api.CreateProjectAsync(acme, "Web");
        await api.CreateProjectAsync(acme, "Mobile");
        var databases = $"/v1/management/organizations/{acme}/projects/{project}/databases";
        using var main = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var mainId = (await main.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        using var organization = await api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "ACME" });
        using var organizationRename = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{globex}"), new { name = "acme" });
        using var organizationOwnCase = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{acme}"), new { name = "ACME" });
        using var projectDuplicate = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{acme}/projects"), new { name = "web" });
        using var projectRename = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{acme}/projects/{project}"), new { name = "MOBILE" });
        using var projectElsewhere = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{globex}/projects"), new { name = "Web" });
        using var databaseDuplicate = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        using var branchDuplicate = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "main" });
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));

        await Assert.That(organization.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(organizationRename.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(organizationOwnCase.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(projectDuplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(projectRename.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(projectElsewhere.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(databaseDuplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(branchDuplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        // A rejected create or branch leaves no database behind.
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Directory.EnumerateFiles(api.Server.DataDirectory, "db-*").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Unknown_resources_are_not_found()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        var unknown = Guid.NewGuid();
        var unknownOrganization = $"/v1/management/organizations/{unknown}";
        var unknownProject = $"/v1/management/organizations/{organization}/projects/{unknown}";
        var unknownDatabase = $"/v1/management/organizations/{organization}/projects/{project}/databases/{unknown}";
        var named = new { name = "valid" };

        HttpResponseMessage[] responses =
        [
            await api.Client.GetAsync(Uri(unknownOrganization)),
            await api.Client.PatchAsJsonAsync(Uri(unknownOrganization), named),
            await api.Client.DeleteAsync(Uri(unknownOrganization)),
            await api.Client.GetAsync(Uri($"{unknownOrganization}/projects")),
            await api.Client.PostAsJsonAsync(Uri($"{unknownOrganization}/projects"), named),
            await api.Client.GetAsync(Uri(unknownProject)),
            await api.Client.PatchAsJsonAsync(Uri(unknownProject), named),
            await api.Client.DeleteAsync(Uri(unknownProject)),
            await api.Client.GetAsync(Uri($"{unknownProject}/databases")),
            await api.Client.PostAsJsonAsync(Uri($"{unknownProject}/databases"), named),
            await api.Client.GetAsync(Uri(unknownDatabase)),
            await api.Client.DeleteAsync(Uri(unknownDatabase)),
            await api.Client.PostAsJsonAsync(Uri($"{unknownDatabase}/branches"), named),
        ];

        foreach (var response in responses)
        {
            using (response)
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
        }
    }

    [Test]
    public async Task Invalid_names_and_queries_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        object[] badDisplayNames = [new { }, new { name = "" }, new { name = "   " }, new { name = "--" }, new { name = new string('a', 129) }, new { name = "a\nb" }];
        object[] badDatabaseNames = [new { }, new { name = "Main" }, new { name = "1db" }, new { name = "con" }, new { name = new string('a', 64) }];

        var statuses = new List<HttpStatusCode>();
        foreach (var body in badDisplayNames)
        {
            statuses.Add(await StatusAsync(api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), body)));
            statuses.Add(await StatusAsync(api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{organization}"), body)));
            statuses.Add(await StatusAsync(api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects"), body)));
            statuses.Add(await StatusAsync(api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"), body)));
        }

        foreach (var body in badDatabaseNames)
        {
            statuses.Add(await StatusAsync(api.Client.PostAsJsonAsync(Uri(databases), body)));
        }

        statuses.Add(await StatusAsync(api.Client.GetAsync(Uri("/v1/management/organizations?limit=0"))));
        statuses.Add(await StatusAsync(api.Client.GetAsync(Uri("/v1/management/organizations?limit=101"))));
        statuses.Add(await StatusAsync(api.Client.PostAsync(Uri("/v1/management/organizations"), new StringContent("{oops", null, "application/json"))));

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.BadRequest));
        // Names are trimmed rather than rejected for surrounding spaces.
        var trimmed = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{await api.CreateOrganizationAsync("  Spaced  ")}"));
        await Assert.That(trimmed.GetProperty("name").GetString()).IsEqualTo("Spaced");
    }

    [Test]
    public async Task Management_needs_the_management_scope_on_the_client_listener()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var anonymous = tls.CreateClient(server.ClientAddress!);
        using var documents = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:read", "documents:write"));
        using var management = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("management"));

        using var noKey = await anonymous.GetAsync(Uri("/v1/management/organizations"));
        using var wrongScope = await documents.GetAsync(Uri("/v1/management/organizations"));
        using var onAdmin = await server.Client.GetAsync(Uri("/v1/management/organizations"));
        using var noDocumentAccess = await management.PutAsync(Uri("/v1/databases/x"), null);

        await Assert.That(noKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(wrongScope.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(noDocumentAccess.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    /// <summary>A server with the client listener and a client holding a <c>management</c> key.</summary>
    private sealed class ManagementApi : IAsyncDisposable
    {
        private readonly ClientTls? _ownedTls;

        private ManagementApi(TerraBaseTestServer server, HttpClient client, ClientTls? ownedTls)
        {
            Server = server;
            Client = client;
            _ownedTls = ownedTls;
        }

        public TerraBaseTestServer Server { get; }

        public HttpClient Client { get; }

        public static async Task<ManagementApi> StartAsync(ClientTls? tls = null)
        {
            var ownedTls = tls is null ? new ClientTls() : null;
            tls ??= ownedTls!;
            var server = await TerraBaseTestServer.StartAsync(tls.Apply);
            return new ManagementApi(server, tls.CreateClient(server.ClientAddress!, await server.IssueAsync("management")), ownedTls);
        }

        public async Task<string> CreateOrganizationAsync(string name)
        {
            using var response = await Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        public async Task<string> CreateProjectAsync(string organization, string name)
        {
            using var response = await Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects"), new { name });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            _ownedTls?.Dispose();
        }
    }
}
