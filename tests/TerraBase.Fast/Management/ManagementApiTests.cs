using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ManagementApiTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Organizations_are_created_listed_and_deleted()
    {
        await using var api = await ManagementApi.StartSharedAsync();
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
    public async Task Projects_are_created_listed_and_deleted()
    {
        await using var api = await ManagementApi.StartSharedAsync();
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
        await using var api = await ManagementApi.StartSharedAsync();
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
        (await ManagementApi.SqlAsync(data, $"{dataApi}/main", "CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT NOT NULL)")).EnsureSuccessStatusCode();
        (await ManagementApi.SqlAsync(data, $"{dataApi}/main", $"INSERT INTO people VALUES ('{p1}', '{person.name.Replace("'", "''", StringComparison.Ordinal)}')")).EnsureSuccessStatusCode();
        using var branched = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "preview" });
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>();
        (await ManagementApi.SqlAsync(data, $"{dataApi}/preview", $"INSERT INTO people VALUES ('{p2}', 'branch')")).EnsureSuccessStatusCode();
        var mainWithoutBranchWrite = await ManagementApi.RowsAsync(data, $"{dataApi}/main", $"SELECT id FROM people WHERE id = '{p2}'");
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));
        using var projectNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{databases}/{mainId}"));
        using var gone = await api.Client.GetAsync(Uri($"{databases}/{mainId}"));
        using var dataGone = await ManagementApi.SqlAsync(data, $"{dataApi}/main", "SELECT 1");
        var branchKeepsCopy = await ManagementApi.RowsAsync(data, $"{dataApi}/preview", $"SELECT id FROM people WHERE id = '{p1}'");

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(main.GetProperty("name").GetString()).IsEqualTo("main");
        await Assert.That(main.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That(EntityIds.IsValid(mainId)).IsTrue();
        await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(EntityIds.IsValid(branch.GetProperty("id").GetString())).IsTrue();
        await Assert.That(branch.GetProperty("branchedFrom").GetString()).IsEqualTo(mainId);
        await Assert.That(mainWithoutBranchWrite.Length).IsEqualTo(0);
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await ErrorAssert.ErrorAsync(projectNotEmpty, HttpStatusCode.Conflict, "not_empty", "delete what it contains first");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(dataGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, organization, project, mainId + ".db"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, organization, project, branch.GetProperty("id").GetString() + ".db"))).IsTrue();
        await Assert.That(branchKeepsCopy.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Users_cannot_reach_organizations_they_are_not_members_of()
    {
        await using var api = await ManagementApi.StartSharedAsync();
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
        await using var api = await ManagementApi.StartSharedAsync();
        using var anonymous = api.WithToken(null);
        using var garbage = api.WithToken(Faker.Random.AlphaNumeric(40));
        using var apiKey = api.DataClient(await api.Server.IssueAsync(await api.CreateOrganizationAsync("Acme"), "documents:read", "documents:write"));
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, Uri("/v1/management/organizations"));
        adminRequest.Headers.Authorization = new("Bearer", api.Issuer.Mint(api.User));

        using var noToken = await anonymous.GetAsync(Uri("/v1/management/organizations"));
        using var notAToken = await garbage.GetAsync(Uri("/v1/management/organizations"));
        using var withApiKey = await apiKey.GetAsync(Uri("/v1/management/organizations"));
        using var onAdmin = await api.Server.Client.SendAsync(adminRequest);
        using var noDataAccess = await ManagementApi.SqlAsync(api.Client, $"/v1/projects/{EntityIds.New()}/databases/x", "SELECT 1");

        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(notAToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withApiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // The internal listener takes only the platform operator, never a ts-auth token.
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // A sign-in token without project_id is not a data credential.
        await Assert.That(noDataAccess.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
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
