using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectTokenTests
{
    private const string AllScopes = "databases:read databases:write sql:read sql:write";

    [Test]
    public async Task A_project_JWT_creates_a_database_and_runs_sql_without_organization_membership()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartSharedAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project, AllScopes));
        var databases = $"/v1/projects/{project}/databases";
        var database = databases + "/main";
        var name = faker.Name.FullName().Replace("'", "''", StringComparison.Ordinal);

        using var created = await client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location?.OriginalString).IsEqualTo(database);
        var info = await created.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(info.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That((await client.GetFromJsonAsync<JsonElement>(Uri(database))).GetProperty("id").GetString())
            .IsEqualTo(info.GetProperty("id").GetString());

        await Assert.That(await StatusAsync(ManagementApi.SqlAsync(client, database, "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL)")))
            .IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StatusAsync(ManagementApi.SqlAsync(client, database, $"INSERT INTO people (name) VALUES ('{name}')"))).IsEqualTo(HttpStatusCode.OK);
        var rows = await ManagementApi.RowsAsync(client, database, "SELECT name FROM people");
        await Assert.That(rows.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Invalid_signature_lifetime_issuer_audience_and_non_ES256_project_tokens_are_rejected()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        var actor = EntityIds.New().ToString();
        var now = DateTime.UtcNow;
        var rsa = api.Issuer.AddKey(SecurityAlgorithms.RsaSha256);
        Action<SecurityTokenDescriptor>[] edits =
        [
            token => (token.IssuedAt, token.NotBefore, token.Expires) = (now.AddMinutes(-10), now.AddMinutes(-10), now.AddMinutes(-2)),
            token => token.Issuer = "https://auth.test/customer-project",
            token => token.Audience = "https://other.test",
            token => token.SigningCredentials = rsa,
            token => token.Claims["project_id"] = 123,
            token => token.Claims["project_id"] = null!,
            token => token.Claims["project_id"] = "00112233-4455-6677-8899-aabbccddeeff",
            token => token.Claims["project_id"] = "01arz3ndektsv4rrffq69g5fav",
            token => token.Claims["project_id"] = "01ARZ3NDEKTSV4RRFFQ69G5FAI",
            token => token.Claims["project_id"] = "81ARZ3NDEKTSV4RRFFQ69G5FAV",
            token => token.Claims["database"] = 123,
            token => token.Claims["database"] = null!,
            token => token.Claims["scope"] = 123,
            token => token.Claims["scope"] = null!,
        ];
        var tokens = edits.Select(edit => Token(api, actor, project, AllScopes, edit: edit)).ToList();
        var valid = Token(api, actor, project, AllScopes);
        tokens.Add(valid[..^2] + (valid[^2] == 'A' ? "B" : "A") + valid[^1]);
        foreach (var token in tokens)
        {
            using var client = api.WithToken(token);
            await Assert.That(await StatusAsync(client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" })))
                .IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using var accepted = api.WithToken(valid);
        await Assert.That(await StatusAsync(accepted.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" })))
            .IsEqualTo(HttpStatusCode.Created);
    }

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

        using var backupResponse = await api.Client.PostAsync(Uri(first.Path + "/backups"), content: null);
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
