using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectCatalogEnrollmentTests
{

    [Test]
    public async Task A_denied_database_create_leaves_a_fresh_project_unenrolled_then_an_unrestricted_create_can_enroll_it()
    {
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

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
