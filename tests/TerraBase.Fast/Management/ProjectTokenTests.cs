using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectTokenTests
{
    private const string AllScopes = "databases:read databases:write documents:read documents:write";

    // Usage, plans and allowances key on the organization ts-auth signs into the project token, so every project of one
    // ts-auth organization lands in it; an organization a legacy commit created with that id keeps its own storage.
    [Test]
    public async Task Enrolled_projects_land_in_the_ts_auth_organization_the_token_signs()
    {
        await using var api = await ManagementApi.StartAsync();
        var actor = EntityIds.New().ToString();
        var organization = EntityIds.New().ToString();
        var catalog = api.Server.Services.GetRequiredService<IProjectCatalog>();
        foreach (var project in new[] { EntityIds.New().ToString(), EntityIds.New().ToString() })
        {
            using var client = api.WithToken(Token(api, actor, project, AllScopes, edit: token => token.Claims["org_id"] = organization));
            using (var created = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" }))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            await Assert.That(await catalog.ProjectOrganizationAsync(project, CancellationToken.None) is string found && found == organization).IsTrue();
        }

        var owned = await api.CreateOrganizationAsync("Legacy " + EntityIds.New().ToString());
        var squatter = EntityIds.New().ToString();
        using var squatting = api.WithToken(Token(api, actor, squatter, AllScopes, edit: token => token.Claims["org_id"] = owned));
        await Assert.That(await StatusAsync(squatting.PostAsJsonAsync(Uri($"/v1/projects/{squatter}/databases"), new { name = "main" })))
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await catalog.ProjectOrganizationAsync(squatter, CancellationToken.None) is not string).IsTrue();
    }

    [Test]
    public async Task A_project_JWT_creates_a_database_and_runs_sql_without_organization_membership()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
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
    public async Task Project_claims_are_checked_before_scope_and_each_operation_requires_its_scope()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var actor = EntityIds.New().ToString();
        using var wrongProject = api.WithToken(Token(api, actor, EntityIds.New().ToString(), scope: null));
        using var noProject = api.WithToken(Token(api, actor, project: null, scope: AllScopes));
        using var noScope = api.WithToken(Token(api, actor, database.Project, scope: null));
        using var readOnly = api.WithToken(Token(api, actor, database.Project, "databases:read documents:read"));

        await Assert.That(await StatusAsync(wrongProject.GetAsync(Uri(database.Data)))).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(noProject.GetAsync(Uri(database.Data)))).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await StatusAsync(ManagementApi.SqlAsync(wrongProject, database, "SELECT 1"))).IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAssert.ErrorAsync(noScope.GetAsync(Uri(database.Data)), HttpStatusCode.Forbidden, "forbidden_scope");
        await ErrorAssert.ErrorAsync(readOnly.PostAsJsonAsync(Uri($"/v1/projects/{database.Project}/databases"), new { name = "other" }),
            HttpStatusCode.Forbidden, "forbidden_scope");
        await Assert.That(await StatusAsync(ManagementApi.SqlAsync(readOnly, database, "SELECT 1"))).IsEqualTo(HttpStatusCode.OK);
        using (var write = await ManagementApi.SqlAsync(readOnly, database, "CREATE TABLE people (id INTEGER PRIMARY KEY)"))
        {
            await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That((await write.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("forbidden_scope");
        }
        var listed = await readOnly.GetFromJsonAsync<JsonElement>(Uri($"/v1/projects/{database.Project}/databases"));
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
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
    public async Task A_database_name_claim_is_rejected_even_when_the_database_exists()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("first");
        using var restricted = api.WithToken(Token(api, api.User, database.Project, AllScopes, database: database.Name));
        foreach (var reference in new[] { database.Name, database.Id })
        {
            using var refused = await restricted.GetAsync(Uri($"/v1/projects/{database.Project}/databases/{reference}"));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
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
