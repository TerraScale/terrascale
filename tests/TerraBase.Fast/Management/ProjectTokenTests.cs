using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectTokenTests
{
    private const string AllScopes = "databases:read databases:write documents:read documents:write";

    [Test]
    public async Task A_project_JWT_creates_and_manages_database_documents_without_organization_membership()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project, AllScopes));
        var databases = $"/v1/projects/{project}/databases";
        var database = databases + "/main";
        var collection = database + "/collections/people";
        var document = collection + "/documents/by-key?id=" + EntityIds.New();
        var name = faker.Name.FullName();

        using var created = await client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location?.OriginalString).IsEqualTo(database);
        var info = await created.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(info.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That((await client.GetFromJsonAsync<JsonElement>(Uri(database))).GetProperty("id").GetString())
            .IsEqualTo(info.GetProperty("id").GetString());

        await Assert.That(await StatusAsync(client.PostAsJsonAsync(Uri(database + "/collections"), new { name = "people" })))
            .IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await StatusAsync(client.PutAsJsonAsync(Uri(document), new { name, count = 1 }))).IsEqualTo(HttpStatusCode.Created);
        var read = await client.GetFromJsonAsync<JsonElement>(Uri(document));
        await Assert.That(read.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        using var patch = JsonContent.Create(new { count = 2 }, mediaType: new MediaTypeHeaderValue("application/merge-patch+json"));
        await Assert.That(await StatusAsync(client.PatchAsync(Uri(document), patch))).IsEqualTo(HttpStatusCode.OK);

        using var query = await client.PostAsJsonAsync(Uri(collection + "/query"), new { filter = new { eq = new object[] { "/count", 2 } } });
        query.EnsureSuccessStatusCode();
        await Assert.That((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength()).IsEqualTo(1);
        var transactionId = EntityIds.New().ToString();
        using var transaction = await client.PostAsJsonAsync(Uri(database + "/transactions"), new
        {
            ops = new object[] { new { op = "create", collection = "people", id = transactionId, document = new { name, count = 3 } } },
        });
        await Assert.That(transaction.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.GetFromJsonAsync<JsonElement>(Uri(collection + "/documents/by-key?id=" + transactionId)))
            .GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(3);
        await Assert.That(await StatusAsync(client.DeleteAsync(Uri(document)))).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StatusAsync(client.GetAsync(Uri(document)))).IsEqualTo(HttpStatusCode.NotFound);
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
        await using var api = await ManagementApi.StartAsync();
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var legacy = await api.CreateClientAsync(first.Organization, "documents:read", "documents:write");
        foreach (var database in new[] { first, second })
        {
            await ManagementApi.CreateCollectionAsync(legacy, database, "people");
            using var written = await legacy.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "existing")), new { name = faker.Name.FullName() });
            written.EnsureSuccessStatusCode();
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
        await Assert.That(usage.GetProperty("documents").GetInt64()).IsEqualTo(1);
        await Assert.That(usage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!))
            .IsEquivalentTo([first.Id]);
        await Assert.That(await StatusAsync(restricted.GetAsync(Uri(first.Data)))).IsEqualTo(HttpStatusCode.OK);
        await ErrorAssert.ErrorAsync(restricted.GetAsync(Uri(second.Data)), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(restricted.DeleteAsync(Uri(second.Data)), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(restricted.PostAsJsonAsync(Uri(second.Data + "/branches"), new { name = "second-copy" }),
            HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await Assert.That(await StatusAsync(restricted.GetAsync(Uri(ManagementApi.DocumentPath(second, "people", "existing")))))
            .IsEqualTo(HttpStatusCode.NotFound);
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
