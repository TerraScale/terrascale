using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectApiTests
{
    private static readonly string[] Name = ["name"];

    [Test]
    public async Task Project_routes_manage_existing_databases_by_name_without_changing_their_data()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var legacy = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(legacy, database, "people");
        var email = new Faker().Internet.Email();
        await JsonAsync(await legacy.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "saved")), new { email }), HttpStatusCode.Created);
        using var client = ProjectClient(api, database.Project);

        var record = await JsonAsync(await client.GetAsync(Uri(database.Data)));
        await Assert.That(record.GetProperty("id").GetString()).IsEqualTo(database.Id);
        var stored = await JsonAsync(await client.GetAsync(Uri(ManagementApi.DocumentPath(database, "people", "saved"))));
        await Assert.That(stored.GetProperty("document").GetProperty("email").GetString()).IsEqualTo(email);
        await JsonAsync(await client.PatchAsJsonAsync(Uri(database.Data), new { replicaRegions = Array.Empty<string>() }));

        var schema = await JsonAsync(await client.GetAsync(Uri(database.Data + "/schema")));
        await Assert.That(schema.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo("people");
        var collection = await JsonAsync(await client.GetAsync(Uri(database.Data + "/schema/people")));
        await Assert.That(collection.GetProperty("documents").GetInt64()).IsEqualTo(1);
        var sql = await JsonAsync(await client.PostAsJsonAsync(Uri(database.Data + "/sql"), new { sql = "SELECT * FROM people ORDER BY email" }));
        await Assert.That(sql.GetProperty("matched").GetInt32()).IsEqualTo(1);
        var check = await JsonAsync(await client.PostAsJsonAsync(Uri(database.Data + "/sql/check"), new { sql = "SELECT * FROM people" }));
        await Assert.That(check.GetProperty("ok").GetBoolean()).IsTrue();

        var recommendations = await JsonAsync(await client.GetAsync(Uri(database.Data + "/index-recommendations")));
        var recommendation = recommendations.GetProperty("items")[0].GetProperty("id").GetString();
        using (var apply = await client.PostAsync(Uri($"{database.Data}/index-recommendations/{recommendation}/apply"), null))
        {
            await Assert.That(apply.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(apply.Headers.Location!.OriginalString).IsEqualTo(database.Data + "/indexes/people_email");
        }

        await JsonAsync(await client.PostAsJsonAsync(Uri(database.Data + "/indexes"), new { collection = "people", fields = Name, name = "people_name" }), HttpStatusCode.Created);
        var indexes = await JsonAsync(await client.GetAsync(Uri(database.Data + "/indexes")));
        await Assert.That(indexes.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await NoContentAsync(await client.DeleteAsync(Uri(database.Data + "/indexes/people_name")));
        var usage = await JsonAsync(await client.GetAsync(Uri(database.Data + "/usage")));
        await Assert.That(usage.GetProperty("databaseId").GetString()).IsEqualTo(database.Id);
        await JsonAsync(await client.GetAsync(Uri($"/v1/projects/{database.Project}/usage")));

        using (var create = await client.PostAsJsonAsync(Uri($"/v1/projects/{database.Project}/databases"), new { name = "created" }))
        {
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(create.Headers.Location!.OriginalString).IsEqualTo($"/v1/projects/{database.Project}/databases/created");
        }

        using (var branch = await client.PostAsJsonAsync(Uri(database.Data + "/branches"), new { name = "branched" }))
        {
            await Assert.That(branch.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(branch.Headers.Location!.OriginalString).IsEqualTo($"/v1/projects/{database.Project}/databases/branched");
        }

        var branched = await JsonAsync(await client.GetAsync(Uri(ManagementApi.DocumentPath(database with { Name = "branched" }, "people", "saved"))));
        await Assert.That(branched.GetProperty("document").GetProperty("email").GetString()).IsEqualTo(email);
        var list = await JsonAsync(await client.GetAsync(Uri($"/v1/projects/{database.Project}/databases")));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(3);
        await NoContentAsync(await client.DeleteAsync(Uri($"/v1/projects/{database.Project}/databases/created")));
        await NoContentAsync(await client.DeleteAsync(Uri($"/v1/projects/{database.Project}/databases/branched")));
        var old = await JsonAsync(await api.Client.GetAsync(Uri(database.Path)));
        await Assert.That(old.GetProperty("id").GetString()).IsEqualTo(database.Id);
    }

    [Test]
    public async Task Project_backup_routes_restore_into_a_named_database_and_leave_the_source_readable()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var name = new Faker().Name.FullName();
        await JsonAsync(await client.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "saved")), new { name }), HttpStatusCode.Created);
        await JsonAsync(await client.GetAsync(Uri(database.Data + "/backup-policy")));
        var policy = await JsonAsync(await client.PutAsJsonAsync(Uri(database.Data + "/backup-policy"), new { fullBackupCron = "0 3 * * *", retentionDays = 3, disabled = true }));
        await Assert.That(policy.GetProperty("disabled").GetBoolean()).IsTrue();

        string backup;
        using (var create = await client.PostAsync(Uri(database.Data + "/backups"), null))
        {
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
            backup = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            await Assert.That(create.Headers.Location!.OriginalString).IsEqualTo($"{database.Data}/backups/{backup}");
        }

        var list = await JsonAsync(await client.GetAsync(Uri(database.Data + "/backups")));
        await Assert.That(list.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == backup)).IsTrue();
        using (var restore = await client.PostAsJsonAsync(Uri($"{database.Data}/backups/{backup}/restore"), new { name = "restored" }))
        {
            await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(restore.Headers.Location!.OriginalString).IsEqualTo($"/v1/projects/{database.Project}/databases/restored");
        }

        var restored = await JsonAsync(await client.GetAsync(Uri(ManagementApi.DocumentPath(database with { Name = "restored" }, "people", "saved"))));
        await Assert.That(restored.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        var source = await JsonAsync(await client.GetAsync(Uri(ManagementApi.DocumentPath(database, "people", "saved"))));
        await Assert.That(source.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
    }

    [Test]
    public async Task Project_saved_query_routes_create_read_update_and_delete_queries()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, database.Project);
        var path = $"/v1/projects/{database.Project}/saved-queries";
        string id;
        using (var create = await client.PostAsJsonAsync(Uri(path), new { name = new Faker().Lorem.Word(), sql = "SELECT * FROM people" }))
        {
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
            id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            await Assert.That(create.Headers.Location!.OriginalString).IsEqualTo($"{path}/{id}");
        }

        var list = await JsonAsync(await client.GetAsync(Uri(path)));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await JsonAsync(await client.GetAsync(Uri($"{path}/{id}")));
        var updated = await JsonAsync(await client.PutAsJsonAsync(Uri($"{path}/{id}"), new { name = "Updated", sql = "SELECT name FROM people" }));
        await Assert.That(updated.GetProperty("name").GetString()).IsEqualTo("Updated");
        await NoContentAsync(await client.DeleteAsync(Uri($"{path}/{id}")));
        using var deleted = await client.GetAsync(Uri($"{path}/{id}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static HttpClient ProjectClient(ManagementApi api, string project) => api.WithToken(api.Issuer.Mint(api.User, token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
    }));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(status).Because(await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private static async Task NoContentAsync(HttpResponseMessage response)
    {
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync());
        }
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
