using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ManagementApiTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    public async Task Organizations_are_created_listed_and_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var name = Faker.Company.CompanyName();

        using var created = await api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name });
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = organization.GetProperty("id").GetString()!;
        using var read = await api.Client.GetAsync(Uri($"/v1/management/organizations/{id}"));
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations")));
        using var deleted = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{id}"));
        using var gone = await api.Client.GetAsync(Uri($"/v1/management/organizations/{id}"));
        using var listAfterDelete = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations")));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"/v1/management/organizations/{id}");
        await Assert.That(organization.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(organization.GetProperty("createdBy").GetString()).IsEqualTo(api.User);
        await Assert.That(organization.GetProperty("plan").GetString()).IsEqualTo("hobby");
        await Assert.That(EntityIds.IsValid(id)).IsTrue();
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(list.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([id]);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(listAfterDelete.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task An_organization_plan_defaults_to_hobby_and_needs_the_service_credential_acting_as_owner()
    {
        await using var api = await ManagementApi.StartAsync();
        using var other = api.As(EntityIds.New().ToString());
        var organization = await api.CreateOrganizationAsync("Acme");
        var plan = $"/v1/management/organizations/{organization}/plan";

        var initial = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));
        using var service = api.Service(api.User);
        using var byOwner = await api.Client.PutAsJsonAsync(Uri(plan), new { plan = "enterprise" });
        using var pro = await service.PutAsJsonAsync(Uri(plan), new { plan = "pro" });
        using var enterprise = await service.PutAsJsonAsync(Uri(plan), new { plan = "enterprise" });
        var read = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));
        using var listed = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations")));
        using var nonMember = await other.PutAsJsonAsync(Uri(plan), new { plan = "hobby" });
        var afterRefused = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));

        await Assert.That(initial.GetProperty("plan").GetString()).IsEqualTo("hobby");
        await Assert.That(pro.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await pro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetString()).IsEqualTo("pro");
        await Assert.That(enterprise.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.GetProperty("plan").GetString()).IsEqualTo("enterprise");
        await Assert.That(listed.RootElement.GetProperty("items")[0].GetProperty("plan").GetString()).IsEqualTo("enterprise");
        await Assert.That(nonMember.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // User tokens cannot grant a resource entitlement, regardless of organization role.
        await ErrorAssert.ErrorAsync(byOwner, HttpStatusCode.Forbidden, "forbidden", "plan changes need the platform operator");
        await Assert.That(afterRefused.GetProperty("plan").GetString()).IsEqualTo("enterprise");
    }

    [Test]
    public async Task A_user_lists_only_their_organizations_in_pages()
    {
        await using var api = await ManagementApi.StartAsync();
        using var other = api.As(EntityIds.New().ToString());
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await api.CreateOrganizationAsync($"Org {i}"));
            await api.CreateOrganizationAsync($"Org {i}", other);
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
    public async Task Projects_are_created_listed_and_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var other = await api.CreateOrganizationAsync("Other");
        var projects = $"/v1/management/organizations/{organization}/projects";

        using var created = await api.Client.PostAsJsonAsync(Uri(projects), new { name = "Production" });
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(projects)));
        var read = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{projects}/{project}"));
        using var underOtherOrganization = await api.Client.GetAsync(Uri($"/v1/management/organizations/{other}/projects/{project}"));
        using var organizationNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{projects}/{project}"));
        using var gone = await api.Client.GetAsync(Uri($"{projects}/{project}"));
        using var organizationEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{projects}/{project}");
        await Assert.That(list.RootElement.GetProperty("items")[0].GetProperty("organizationId").GetString()).IsEqualTo(organization);
        await Assert.That(read.GetProperty("name").GetString()).IsEqualTo("Production");
        await Assert.That(underOtherOrganization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAssert.ErrorAsync(organizationNotEmpty, HttpStatusCode.Conflict, "not_empty", "delete what it contains first");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(organizationEmpty.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Databases_are_created_branched_and_deleted_with_their_data()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Shop");
        using var data = api.DataClient(await api.Server.IssueAsync(organization, "documents:read", "documents:write"));
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        var dataApi = $"/v1/projects/{project}/databases";
        var person = new { name = Faker.Name.FullName() };
        var (p1, p2) = (EntityIds.New().ToString(), EntityIds.New().ToString());

        using var created = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var main = await created.Content.ReadFromJsonAsync<JsonElement>();
        var mainId = main.GetProperty("id").GetString()!;
        (await data.PostAsJsonAsync(Uri($"{dataApi}/main/collections"), new { name = "people" })).EnsureSuccessStatusCode();
        (await data.PutAsJsonAsync(Uri($"{dataApi}/main/collections/people/documents/by-key?id={p1}"), person)).EnsureSuccessStatusCode();
        using var branched = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "preview" });
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>();
        (await data.PutAsJsonAsync(Uri($"{dataApi}/preview/collections/people/documents/by-key?id={p2}"), person)).EnsureSuccessStatusCode();
        using var mainWithoutBranchWrite = await data.GetAsync(Uri($"{dataApi}/main/collections/people/documents/by-key?id={p2}"));
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));
        using var projectNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{databases}/{mainId}"));
        using var gone = await api.Client.GetAsync(Uri($"{databases}/{mainId}"));
        using var dataGone = await data.GetAsync(Uri($"{dataApi}/main/collections/people/documents/by-key?id={p1}"));
        using var branchKeepsCopy = await data.GetAsync(Uri($"{dataApi}/preview/collections/people/documents/by-key?id={p1}"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(main.GetProperty("name").GetString()).IsEqualTo("main");
        await Assert.That(main.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That(EntityIds.IsValid(mainId)).IsTrue();
        await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(EntityIds.IsValid(branch.GetProperty("id").GetString())).IsTrue();
        await Assert.That(branch.GetProperty("branchedFrom").GetString()).IsEqualTo(mainId);
        await Assert.That(mainWithoutBranchWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await ErrorAssert.ErrorAsync(projectNotEmpty, HttpStatusCode.Conflict, "not_empty", "delete what it contains first");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(dataGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, organization, project, mainId + ".db"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, organization, project, branch.GetProperty("id").GetString() + ".db"))).IsTrue();
        await Assert.That(branchKeepsCopy.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Duplicate_names_conflict_ignoring_case_within_the_same_parent()
    {
        await using var api = await ManagementApi.StartAsync();
        using var otherUser = api.As(EntityIds.New().ToString());
        var acme = await api.CreateOrganizationAsync("Acme");
        var globex = await api.CreateOrganizationAsync("Globex");
        var project = await api.CreateProjectAsync(acme, "Web");
        await api.CreateProjectAsync(acme, "Mobile");
        var databases = $"/v1/management/organizations/{acme}/projects/{project}/databases";
        using var main = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var mainId = (await main.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        using var organization = await api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "ACME" });
        using var organizationOtherUser = await otherUser.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "Acme" });
        using var projectDuplicate = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{acme}/projects"), new { name = "web" });
        using var projectElsewhere = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{globex}/projects"), new { name = "Web" });
        using var databaseDuplicate = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        using var branchDuplicate = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "main" });
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));

        await ErrorAssert.ErrorAsync(organization, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        // Organization names are unique per creator, so they reveal nothing about other tenants.
        await Assert.That(organizationOtherUser.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await ErrorAssert.ErrorAsync(projectDuplicate, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await Assert.That(projectElsewhere.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await ErrorAssert.ErrorAsync(databaseDuplicate, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await ErrorAssert.ErrorAsync(branchDuplicate, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        // A rejected create or branch leaves no database behind.
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        // Database files only: an open database also has Turso -wal and -shm files.
        await Assert.That(Directory.EnumerateFiles(Path.Combine(api.Server.DataDirectory, acme, project), "*.db").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Unknown_resources_are_not_found()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        var unknown = EntityIds.New().ToString();

        var statuses = await AllStatusesAsync(api.Client, $"/v1/management/organizations/{unknown}", unknown.ToString(), unknown.ToString());
        statuses.AddRange(await AllStatusesAsync(api.Client, $"/v1/management/organizations/{organization}", unknown.ToString(), unknown.ToString(), Level.Project));
        statuses.AddRange(await AllStatusesAsync(api.Client, $"/v1/management/organizations/{organization}", project, unknown.ToString(), Level.Database));

        await Assert.That(statuses).Count().IsEqualTo(12 + 7 + 3);
        await Assert.That(statuses.Distinct()).IsEquivalentTo([HttpStatusCode.NotFound]);
    }

    [Test]
    public async Task Users_cannot_reach_organizations_they_are_not_members_of()
    {
        await using var api = await ManagementApi.StartAsync();
        using var intruder = api.As(EntityIds.New().ToString());
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        using var created = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main" });
        var database = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var statuses = await AllStatusesAsync(intruder, $"/v1/management/organizations/{organization}", project, database);
        await Assert.That(statuses).Count().IsEqualTo(12);
        using var intruderList = JsonDocument.Parse(await intruder.GetStringAsync(Uri("/v1/management/organizations")));
        using var stillThere = await api.Client.GetAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}/databases/{database}"));
        var unchanged = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));

        await Assert.That(statuses.Distinct()).IsEquivalentTo([HttpStatusCode.NotFound]);
        await Assert.That(intruderList.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(stillThere.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unchanged.GetProperty("name").GetString()).IsEqualTo("Acme");
        await Assert.That(unchanged.GetProperty("plan").GetString()).IsEqualTo("hobby");
    }

    [Test]
    public async Task Management_needs_a_user_token_on_the_client_listener()
    {
        await using var api = await ManagementApi.StartAsync();
        using var anonymous = api.WithToken(null);
        using var garbage = api.WithToken(Faker.Random.AlphaNumeric(40));
        using var apiKey = api.DataClient(await api.Server.IssueAsync(await api.CreateOrganizationAsync("Acme"), "documents:read", "documents:write"));
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, Uri("/v1/management/organizations"));
        adminRequest.Headers.Authorization = new("Bearer", api.Issuer.Mint(api.User));

        using var noToken = await anonymous.GetAsync(Uri("/v1/management/organizations"));
        using var notAToken = await garbage.GetAsync(Uri("/v1/management/organizations"));
        using var withApiKey = await apiKey.GetAsync(Uri("/v1/management/organizations"));
        using var onAdmin = await api.Server.Client.SendAsync(adminRequest);
        using var noDocumentAccess = await api.Client.PutAsync(Uri($"/v1/projects/{EntityIds.New()}/databases/x"), null);

        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(notAToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withApiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // The internal listener takes only the platform operator, never a ts-auth token.
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // A sign-in token without project_id is not a data credential.
        await Assert.That(noDocumentAccess.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Management_is_disabled_without_an_auth_issuer()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, issuer.Route);
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        using var response = await client.GetAsync(Uri("/v1/management/organizations"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Operator_only_system_documents_allow_fresh_catalog_initialization()
    {
        await using var api = await ManagementApi.StartAsync();
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var id = EntityIds.New().ToString();
        var key = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            name = "Operator key",
            scopes = ReadScopes,
            organizationId = EntityIds.New().ToString(),
        });
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(await transaction.GetAsync("catalog", "version", CancellationToken.None)).IsNull();
            await transaction.PutAsync("api_keys", id, key, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        using var listed = await api.Client.GetAsync(Uri("/v1/management/organizations"));
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var organization = await api.CreateOrganizationAsync("Fresh");
        using var read = await api.Client.GetAsync(Uri($"/v1/management/organizations/{organization}"));
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await using var transactionAfterInitialization = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await transactionAfterInitialization.GetAsync("catalog", "version", CancellationToken.None)).IsNotNull();
        var preserved = await transactionAfterInitialization.GetAsync("api_keys", id, CancellationToken.None);
        await Assert.That(preserved).IsNotNull();
        // The catalog stores canonical JSON (fields in name order), like every lattice database.
        using var stored = JsonDocument.Parse(preserved!.Json);
        using var written = JsonDocument.Parse(key);
        await Assert.That(JsonElement.DeepEquals(stored.RootElement, written.RootElement)).IsTrue();
    }

    // Seeds current-format system documents after real-server bootstrap.
    // In a data directory a server of this build already started on: an unprepared directory with files is refused.
    // Every document of the _system database, as "collection|id|body" lines in key order.
    // Every route under one organization from `from` down, addressing the given project and database ids.
    private static async Task<List<HttpStatusCode>> AllStatusesAsync(
        HttpClient client, string organization, string project, string database, Level from = Level.Organization)
    {
        var named = new { name = "valid" };
        var projectPath = $"{organization}/projects/{project}";
        var databasePath = $"{projectPath}/databases/{database}";
        (Level Level, Func<Task<HttpResponseMessage>> Send)[] requests =
        [
            (Level.Organization, () => client.GetAsync(Uri(organization))),
            (Level.Organization, () => client.PutAsJsonAsync(Uri($"{organization}/plan"), new { plan = "pro" })),
            (Level.Organization, () => client.DeleteAsync(Uri(organization))),
            (Level.Organization, () => client.GetAsync(Uri($"{organization}/projects"))),
            (Level.Organization, () => client.PostAsJsonAsync(Uri($"{organization}/projects"), named)),
            (Level.Project, () => client.GetAsync(Uri(projectPath))),
            (Level.Project, () => client.DeleteAsync(Uri(projectPath))),
            (Level.Project, () => client.GetAsync(Uri($"{projectPath}/databases"))),
            (Level.Project, () => client.PostAsJsonAsync(Uri($"{projectPath}/databases"), named)),
            (Level.Database, () => client.GetAsync(Uri(databasePath))),
            (Level.Database, () => client.DeleteAsync(Uri(databasePath))),
            (Level.Database, () => client.PostAsJsonAsync(Uri($"{databasePath}/branches"), named)),
        ];
        var statuses = new List<HttpStatusCode>();
        foreach (var (level, send) in requests.Where(request => request.Level >= from))
        {
            using var response = await send();
            statuses.Add(response.StatusCode);
            await ErrorAssert.ErrorAsync(response, HttpStatusCode.NotFound, "not_found", "the resource does not exist");
        }

        return statuses;
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private enum Level
    {
        Organization,
        Project,
        Database,
    }
}
