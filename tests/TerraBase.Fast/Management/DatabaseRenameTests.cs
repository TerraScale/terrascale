using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

// docs/terrabase/database-rename.md, in one region.
public sealed class DatabaseRenameTests
{
    private const string Scopes = "databases:read databases:write documents:read documents:write";

    [Test]
    public async Task Rename_keeps_identity_data_placement_and_ulid_keys_and_frees_the_old_name()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("original");
        using var client = Client(api, database.Project);
        using var restricted = Client(api, database.Project, database.Id);
        var root = $"/v1/projects/{database.Project}/databases";
        var stable = root + "/" + database.Id;
        var before = await ReadAsync(await client.GetAsync(Uri(stable)));
        using (var mixed = await restricted.PatchAsJsonAsync(Uri(stable), new { name = "ignored", allRegions = true }))
        {
            await Assert.That(mixed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var document = EntityIds.New().ToString();
        var email = new Faker().Internet.Email();
        await ReadAsync(await restricted.PutAsJsonAsync(Uri($"{stable}/collections/people/documents/by-key?id={document}"), new { email }), HttpStatusCode.Created);

        var renamed = await ReadAsync(await restricted.PatchAsJsonAsync(Uri(stable), new { name = "renamed" }));
        await Assert.That(renamed.GetProperty("name").GetString()).IsEqualTo("renamed");
        foreach (var field in new[] { "id", "projectId", "createdAt", "homeRegion", "replicaRegions", "allRegions", "branchedFrom" })
        {
            await Assert.That(renamed.GetProperty(field).GetRawText()).IsEqualTo(before.GetProperty(field).GetRawText());
        }

        var repeated = await ReadAsync(await restricted.PatchAsJsonAsync(Uri(stable), new { name = "renamed" }));
        await Assert.That(repeated.GetRawText()).IsEqualTo(renamed.GetRawText());
        foreach (var reference in new[] { database.Id, "renamed" })
        {
            var saved = await ReadAsync(await restricted.GetAsync(Uri($"{root}/{reference}/collections/people/documents/by-key?id={document}")));
            await Assert.That(saved.GetProperty("document").GetProperty("email").GetString()).IsEqualTo(email);
        }

        using (var old = await client.GetAsync(Uri(root + "/original")))
        {
            await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // The released name serves a new database only; the ULID-restricted key stays on the renamed one.
        var other = await ReadAsync(await client.PostAsJsonAsync(Uri(root), new { name = "original" }), HttpStatusCode.Created);
        using (var rebound = await restricted.GetAsync(Uri(root + "/original")))
        {
            await Assert.That(rebound.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using (var duplicate = await client.PatchAsJsonAsync(Uri(stable), new { name = "original" }))
        {
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That((await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetRawText()).Contains("name_taken");
        }

        await Assert.That((await ReadAsync(await client.GetAsync(Uri(root + "/original")))).GetProperty("id").GetString()).IsEqualTo(other.GetProperty("id").GetString());
        await Assert.That((await ReadAsync(await restricted.GetAsync(Uri(stable)))).GetProperty("name").GetString()).IsEqualTo("renamed");
    }

    [Test]
    [Arguments("")]
    [Arguments("Invalid")]
    [Arguments("has space")]
    [Arguments("con")]
    [Arguments("name\n")]
    public async Task Invalid_names_leave_the_current_name_unchanged(string name)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("original");
        using var client = Client(api, database.Project);
        using var invalid = await client.PatchAsJsonAsync(Uri(database.Data), new { name });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await ReadAsync(await client.GetAsync(Uri(database.Data)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
    }

    private static HttpClient Client(ManagementApi api, string project, string? database = null) => api.WithToken(api.Issuer.Mint(api.User, token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = Scopes;
        if (database is not null)
        {
            token.Claims["database"] = database;
        }
    }));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(expected).Because(await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
