using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ManagementDatabaseLifecycleTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Databases_are_created_branched_and_deleted_with_their_data()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Shop");
        using var data = api.DataClient(await api.Server.IssueAsync(organization, "sql:read", "sql:write"));
        var databases = $"/v1/projects/{project}/databases";
        var dataApi = $"/v1/projects/{project}/databases";
        using var management = api.ProjectClient(project);
        var person = new { name = Faker.Name.FullName() };
        var (p1, p2) = (EntityIds.New().ToString(), EntityIds.New().ToString());

        using var created = await management.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var main = await created.Content.ReadFromJsonAsync<JsonElement>();
        var mainId = main.GetProperty("id").GetString()!;
        (await ManagementApi.SqlAsync(data, $"{dataApi}/main", "CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT NOT NULL)")).EnsureSuccessStatusCode();
        (await ManagementApi.SqlAsync(data, $"{dataApi}/main", $"INSERT INTO people VALUES ('{p1}', '{person.name.Replace("'", "''", StringComparison.Ordinal)}')")).EnsureSuccessStatusCode();
        using var branched = await management.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "preview" });
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>();
        (await ManagementApi.SqlAsync(data, $"{dataApi}/preview", $"INSERT INTO people VALUES ('{p2}', 'branch')")).EnsureSuccessStatusCode();
        var mainWithoutBranchWrite = await ManagementApi.RowsAsync(data, $"{dataApi}/main", $"SELECT id FROM people WHERE id = '{p2}'");
        using var list = JsonDocument.Parse(await management.GetStringAsync(Uri(databases)));
        using var projectNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"));
        using var deleted = await management.DeleteAsync(Uri($"{databases}/{mainId}"));
        using var gone = await management.GetAsync(Uri($"{databases}/{mainId}"));
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

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
