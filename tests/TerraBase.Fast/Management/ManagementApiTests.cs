using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;

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
    public async Task Users_cannot_reach_organizations_they_are_not_members_of()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        using var intruder = api.As(EntityIds.New().ToString());
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        using var created = await api.Client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" });
        var database = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var statuses = await AllStatusesAsync(intruder, $"/v1/management/organizations/{organization}", project, database);
        await Assert.That(statuses).Count().IsEqualTo(12);
        using var intruderList = JsonDocument.Parse(await intruder.GetStringAsync(Uri("/v1/management/organizations")));
        using var stillThere = await api.Client.GetAsync(Uri($"/v1/projects/{project}/databases/{database}"));
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
        using var apiKey = api.DataClient(await api.Server.IssueAsync(await api.CreateOrganizationAsync("Acme"), "sql:read", "sql:write"));
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, Uri("/v1/management/organizations"));
        adminRequest.Headers.Authorization = new("Bearer", api.Issuer.Mint(api.User));

        using var noToken = await anonymous.GetAsync(Uri("/v1/management/organizations"));
        using var notAToken = await garbage.GetAsync(Uri("/v1/management/organizations"));
        using var withApiKey = await apiKey.GetAsync(Uri("/v1/management/organizations"));
        using var onAdmin = await api.Server.Client.SendAsync(adminRequest);
        using var noDataAccess = await ManagementApi.SqlAsync(api.Client, $"/v1/projects/{EntityIds.New()}/databases/x", "SELECT 1");
        var keyShaped = EntityIds.New() + "_" + Faker.Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
        using var rawKey = api.WithToken(keyShaped);
        using var withRawKey = await rawKey.GetAsync(Uri("/v1/management/organizations"));

        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(notAToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withApiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(noToken.Headers.WwwAuthenticate.Single().ToString())
            .IsEqualTo("Bearer realm=\"terrascale\"");
        await Assert.That(notAToken.Headers.WwwAuthenticate.Single().ToString())
            .IsEqualTo("Bearer realm=\"terrascale\", error=\"invalid_token\"");
        await Assert.That(withRawKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withRawKey.Headers.WwwAuthenticate.Single().ToString())
            .IsEqualTo("Bearer realm=\"terrascale\", error=\"invalid_token\"");
        var keyBody = await withRawKey.Content.ReadAsStringAsync();
        var keyError = JsonElement.Parse(keyBody).GetProperty("error");
        await Assert.That(keyError.GetProperty("code").GetString()).IsEqualTo("invalid_token");
        await Assert.That(keyError.GetProperty("message").GetString())
            .IsEqualTo("This is an API key; exchange it at the token endpoint and send the short-lived access token instead.");
        await Assert.That(keyBody).DoesNotContain(keyShaped);
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(onAdmin.Headers.WwwAuthenticate).IsEmpty();
        await Assert.That(noDataAccess.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static async Task<List<HttpStatusCode>> AllStatusesAsync(
        HttpClient client, string organization, string project, string database, Level from = Level.Organization)
    {
        var named = new { name = "valid" };
        var projectPath = $"{organization}/projects/{project}";
        var databases = $"/v1/projects/{project}/databases";
        var databasePath = $"{databases}/{database}";
        (Level Level, string Code, string Message, Func<Task<HttpResponseMessage>> Send)[] requests =
        [
            (Level.Organization, "not_found", "the resource does not exist", () => client.GetAsync(Uri(organization))),
            (Level.Organization, "not_found", "the resource does not exist", () => client.PutAsJsonAsync(Uri($"{organization}/plan"), new { plan = "pro" })),
            (Level.Organization, "not_found", "the resource does not exist", () => client.DeleteAsync(Uri(organization))),
            (Level.Organization, "not_found", "the resource does not exist", () => client.GetAsync(Uri($"{organization}/projects"))),
            (Level.Organization, "not_found", "the resource does not exist", () => client.PostAsJsonAsync(Uri($"{organization}/projects"), named)),
            (Level.Project, "not_found", "the resource does not exist", () => client.GetAsync(Uri(projectPath))),
            (Level.Project, "not_found", "the resource does not exist", () => client.DeleteAsync(Uri(projectPath))),
            (Level.Project, "unknown_database", "the database does not exist", () => client.GetAsync(Uri(databases))),
            (Level.Project, "unknown_database", "the database does not exist", () => client.PostAsJsonAsync(Uri(databases), named)),
            (Level.Database, "unknown_database", "the database does not exist", () => client.GetAsync(Uri(databasePath))),
            (Level.Database, "unknown_database", "the database does not exist", () => client.DeleteAsync(Uri(databasePath))),
            (Level.Database, "unknown_database", "the database does not exist", () => client.PostAsJsonAsync(Uri($"{databasePath}/branches"), named)),
        ];
        var statuses = new List<HttpStatusCode>();
        foreach (var (level, code, message, send) in requests.Where(request => request.Level >= from))
        {
            using var response = await send();
            statuses.Add(response.StatusCode);
            await ErrorAssert.ErrorAsync(response, HttpStatusCode.NotFound, code, message);
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
