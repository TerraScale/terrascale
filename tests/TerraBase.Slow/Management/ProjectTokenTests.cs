using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;

namespace TerraBase.Fast.Management;

public sealed class ProjectTokenTests
{
    private const string AllScopes = "databases:read databases:write sql:read sql:write";

    [Test]
    public async Task A_database_restricted_JWT_filters_project_list_and_usage_and_cannot_read_delete_create_branch_or_restore_another_database()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartSharedAsync();
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var legacy = await api.CreateClientAsync(first.Organization, "sql:read", "sql:write");
        foreach (var database in new[] { first, second })
        {
            (await ManagementApi.SqlAsync(legacy, database, "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)")).EnsureSuccessStatusCode();
            (await ManagementApi.SqlAsync(legacy, database, $"INSERT INTO people (name) VALUES ('{faker.Name.FirstName()}')")).EnsureSuccessStatusCode();
        }

        using var backupResponse = await api.ProjectClient(first.Project).PostAsync(Uri(first.Path + "/backups"), content: null);
        backupResponse.EnsureSuccessStatusCode();
        var backup = (await backupResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        using var restricted = api.WithToken(Token(api, EntityIds.New().ToString(), first.Project, AllScopes, database: first.Id));
        var databases = $"/v1/projects/{first.Project}/databases";
        var listed = await restricted.GetFromJsonAsync<JsonElement>(Uri(databases));
        await Assert.That(listed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["first"]);
        var usage = await restricted.GetFromJsonAsync<JsonElement>(Uri($"/v1/projects/{first.Project}/usage"));
        await Assert.That(usage.GetProperty("databases").GetInt64()).IsEqualTo(1);
        await Assert.That(usage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!))
            .IsEquivalentTo([first.Id]);
        await Assert.That(await StatusAsync(restricted.GetAsync(Uri(first.Data)))).IsEqualTo(HttpStatusCode.OK);
        await ErrorAssert.ErrorAsync(restricted.GetAsync(Uri(second.Data)), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(restricted.DeleteAsync(Uri(second.Data)), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(restricted.PostAsJsonAsync(Uri(second.Data + "/branches"), new { name = "second-copy" }),
            HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await Assert.That(await StatusAsync(ManagementApi.SqlAsync(restricted, second, "SELECT * FROM people"))).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(restricted.PostAsJsonAsync(Uri(databases), new { name = "created" }))).IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAssert.ErrorAsync(restricted.PostAsJsonAsync(Uri(first.Data + "/branches"), new { name = "branched" }),
            HttpStatusCode.NotFound, "unknown_route");
        await Assert.That(await StatusAsync(restricted.PostAsJsonAsync(Uri(first.Data + $"/backups/{backup}/restore"), new { name = "restored" })))
            .IsEqualTo(HttpStatusCode.NotFound);

        using var unrestricted = api.WithToken(Token(api, EntityIds.New().ToString(), first.Project, AllScopes));
        var unchanged = await unrestricted.GetFromJsonAsync<JsonElement>(Uri(databases));
        await Assert.That(unchanged.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(unchanged.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!))
            .IsEquivalentTo([first.Id, second.Id]);
    }

    private static string Token(ManagementApi api, string actor, string? project, string? scope, string? database = null, Action<SecurityTokenDescriptor>? edit = null) =>
        api.Issuer.Mint(actor, token =>
        {
            if (project is not null)
            {
                token.Claims["project_id"] = project;
            }

            if (scope is not null)
            {
                token.Claims["scope"] = scope;
            }

            if (database is not null)
            {
                token.Claims["database"] = database;
            }

            edit?.Invoke(token);
        });

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        return response.StatusCode;
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
