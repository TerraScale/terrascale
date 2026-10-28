using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectCatalogEnrollmentTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_chosen_legacy_organization_UUID_cannot_claim_a_new_signed_project(bool deleteLegacyOrganization)
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        var organization = $"/v1/management/organizations/{project}";
        var legacyProject = organization + $"/projects/{project}";
        var legacyName = faker.Company.CompanyName();
        await JsonAsync(api.Client.PostAsJsonAsync(Uri("/v1/management/commits"), new
        {
            id = EntityIds.New().ToString(),
            ops = new[] { new { op = "create_organization", id = project, name = legacyName } },
        }));
        using var legacyData = await api.CreateClientAsync(project, "documents:read", "documents:write");
        if (deleteLegacyOrganization)
        {
            using var deleted = await api.Client.DeleteAsync(Uri(organization));
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        using var client = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
        }));
        var databases = $"/v1/projects/{project}/databases";
        var database = databases + "/main";
        var created = await JsonAsync(client.PostAsJsonAsync(Uri(databases), new { name = "main" }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString();
        await JsonAsync(client.PostAsJsonAsync(Uri(database + "/collections"), new { name = "people" }), HttpStatusCode.Created);
        var document = database + "/collections/people/documents/by-key?id=" + EntityIds.New();
        var name = faker.Name.FullName();
        await JsonAsync(client.PutAsJsonAsync(Uri(document), new { name }), HttpStatusCode.Created);
        await Assert.That((await JsonAsync(client.GetAsync(Uri(document)))).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);

        // Membership and keys of the live organization, or retained keys of its tombstone, confer no project access.
        await NotFoundAsync(api.Client.GetAsync(Uri(legacyProject)));
        await NotFoundAsync(api.Client.GetAsync(Uri(legacyProject + "/databases")));
        await NotFoundAsync(api.Client.GetAsync(Uri(legacyProject + "/databases/" + id)));
        await NotFoundAsync(api.Client.PostAsJsonAsync(Uri(legacyProject + $"/databases/{id}/sql"), new { sql = "SELECT * FROM people" }));
        await ErrorAssert.ErrorAsync(legacyData.GetAsync(Uri(document)), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(legacyData.PutAsJsonAsync(Uri(document), new { name = "changed" }), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await Assert.That((await JsonAsync(client.GetAsync(Uri(document)))).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);

        if (deleteLegacyOrganization)
        {
            await NotFoundAsync(api.Client.GetAsync(Uri(organization)));
        }
        else
        {
            var preserved = await JsonAsync(api.Client.GetAsync(Uri(organization)));
            await Assert.That(preserved.GetProperty("name").GetString()).IsEqualTo(legacyName);
            await Assert.That(preserved.GetProperty("createdBy").GetString()).IsEqualTo(api.User);
        }

        // A second authorized create resolves the persisted mapping instead of allocating another namespace.
        await JsonAsync(client.PostAsJsonAsync(Uri(databases), new { name = "other" }), HttpStatusCode.Created);
        var listed = await JsonAsync(client.GetAsync(Uri(databases)));
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That((await JsonAsync(client.GetAsync(Uri(database)))).GetProperty("id").GetString()).IsEqualTo(id);
    }

    [Test]
    public async Task A_denied_database_create_leaves_a_fresh_project_unenrolled_then_an_unrestricted_create_can_enroll_it()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        var actor = EntityIds.New().ToString();
        using var restricted = api.WithToken(api.Issuer.Mint(actor, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["database"] = EntityIds.New().ToString();
            token.Claims["scope"] = "databases:read databases:write";
        }));
        using var unrestricted = api.WithToken(api.Issuer.Mint(actor, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:read";
        }));
        var databases = Uri($"/v1/projects/{project}/databases");
        await ErrorAssert.ErrorAsync(restricted.PostAsJsonAsync(databases, new { name = "second" }), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(unrestricted.GetAsync(databases), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");

        // Verify persisted side effects after the real request, including records an unenrolled JWT cannot read.
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await system.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await transaction.GetAsync("project_organizations", project, CancellationToken.None)).IsNull();
            await Assert.That(await transaction.GetAsync($"projects/{project}/binding", "ts_auth", CancellationToken.None)).IsNull();
            var organizations = await transaction.ListAsync("organizations", afterId: null, limit: 100, maxBytes: 1024 * 1024, CancellationToken.None);
            await Assert.That(organizations.Documents.Count).IsEqualTo(0);
        }

        using var creator = api.WithToken(api.Issuer.Mint(actor, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:write";
        }));
        using var created = await creator.PostAsJsonAsync(databases, new { name = "first" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        var listed = await JsonAsync(unrestricted.GetAsync(databases));
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo("first");
    }

    private static async Task<JsonElement> JsonAsync(Task<HttpResponseMessage> sending, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task NotFoundAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
