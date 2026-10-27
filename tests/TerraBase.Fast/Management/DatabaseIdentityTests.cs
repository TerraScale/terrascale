using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class DatabaseIdentityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Database_ids_and_names_reach_the_same_data_and_preserve_database_restrictions(bool idRoute)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("original");
        var other = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "other");
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = Client(api, database.Project);
        using var restricted = Client(api, database.Project, database.Id);
        var root = $"/v1/projects/{database.Project}/databases/";
        var target = root + (idRoute ? database.Id : database.Name);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var id = EntityIds.New().ToString();
        var email = new Faker().Internet.Email();
        using var saved = await restricted.PutAsJsonAsync(Uri($"{target}/collections/people/documents/by-key?id={id}"), new { email });
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await saved.Content.ReadAsStringAsync());
        foreach (var reference in new[] { database.Id, database.Name })
        {
            using var metadata = await restricted.GetAsync(Uri(root + reference));
            await Assert.That(metadata.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await metadata.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("id").GetString()).IsEqualTo(database.Id);
            await Assert.That(body.GetProperty("name").GetString()).IsEqualTo(database.Name);
            using var read = await restricted.GetAsync(Uri($"{root}{reference}/collections/people/documents/by-key?id={id}"));
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var document = await read.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(document.GetProperty("document").GetProperty("email").GetString()).IsEqualTo(email);
        }
        foreach (var reference in new[] { other.Id, other.Name })
        {
            using var refused = await restricted.GetAsync(Uri(root + reference));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(refused.Headers.Contains("X-TerraBase-Database-Home")).IsFalse();
            using var refusedData = await restricted.GetAsync(Uri($"{root}{reference}/collections/people/documents/by-key?id={id}"));
            await Assert.That(refusedData.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(refusedData.Headers.Contains("X-TerraBase-Database-Home")).IsFalse();
        }
        using var foreign = Client(api, EntityIds.New().ToString(), database.Id);
        using var wrongProject = await foreign.GetAsync(Uri(target));
        await Assert.That(wrongProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var readOnly = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["database"] = database.Id;
            token.Claims["scope"] = "databases:read";
        }));
        using var noScope = await readOnly.GetAsync(Uri($"{target}/collections/people/documents/by-key?id={id}"));
        await Assert.That(noScope.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        // Renaming keeps the immutable restriction and releases the former name.
        using var rename = await restricted.PatchAsJsonAsync(Uri(target), new { name = "renamed" });
        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var renamed = await restricted.GetAsync(Uri(root + "renamed"));
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsEqualTo(database.Id);
        using var released = await restricted.GetAsync(Uri(root + database.Name));
        await Assert.That(released.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static HttpClient Client(ManagementApi api, string project, string? restriction = null) => api.WithToken(api.Issuer.Mint(api.User, token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
        if (restriction is not null)
        {
            token.Claims["database"] = restriction;
        }
    }));

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
