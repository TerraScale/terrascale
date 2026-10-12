using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CredentialApiTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] ReadWrite = ["documents:read", "documents:write"];
    private static readonly string[] ReadOnly = ["documents:read"];
    private static readonly string[] ManagementScope = ["management"];
    private static readonly string[] UnknownScope = ["documents:read", "admin"];

    [Test]
    public async Task Key_pagination_survives_restart_with_the_clock_behind()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-key-restart-").FullName;
        try
        {
            var frozenAt = new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero);
            var issued = new List<string>();
            string user, path, cursor;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider(frozenAt)))
            {
                user = api.User;
                var database = await api.CreateManagedDatabaseAsync();
                path = database.ProjectPath + "/api-keys";
                for (var i = 0; i < 3; i++)
                {
                    using var created = await api.Client.PostAsJsonAsync(Uri(path), new { name = $"key-{i}", scopes = ReadOnly });
                    created.EnsureSuccessStatusCode();
                    issued.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetProperty("id").GetString()!);
                }

                cursor = (await api.Client.GetFromJsonAsync<JsonElement>(Uri(path + "?limit=1"))).GetProperty("next").GetString()!;
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider(frozenAt.AddDays(-1))))
            {
                using var owner = api.As(user);
                using var created = await owner.PostAsJsonAsync(Uri(path), new { name = "after-restart", scopes = ReadOnly });
                created.EnsureSuccessStatusCode();
                issued.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetProperty("id").GetString()!);
                var page = await owner.GetFromJsonAsync<JsonElement>(Uri($"{path}?after={cursor}"));
                await Assert.That(string.Join(',', page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())))
                    .IsEqualTo(string.Join(',', issued.Skip(1)));
                await Assert.That(page.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Keys_created_at_one_timestamp_paginate_in_committed_creation_order()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        var path = database.ProjectPath + "/api-keys";
        var issued = new List<string>();
        for (var i = 0; i < 16; i++)
        {
            using var created = await api.Client.PostAsJsonAsync(Uri(path), new { name = $"key-{i}", scopes = ReadOnly });
            created.EnsureSuccessStatusCode();
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            issued.Add(body.GetProperty("key").GetProperty("id").GetString()!);
            await Assert.That(body.GetProperty("key").GetProperty("createdAt").GetDateTimeOffset()).IsEqualTo(time.GetUtcNow());
        }

        var first = await api.Client.GetFromJsonAsync<JsonElement>(Uri(path + "?limit=5"));
        var listed = first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList();
        var next = first.GetProperty("next").GetString();
        // Cursors follow the persisted sequence independently of the key ULID.
        await Assert.That(next).IsEqualTo("0000000000000000004");
        time.AdjustTime(time.GetUtcNow().AddDays(-1));
        using var appended = await api.Client.PostAsJsonAsync(Uri(path), new { name = "after-first-page", scopes = ReadOnly });
        appended.EnsureSuccessStatusCode();
        issued.Add((await appended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetProperty("id").GetString()!);
        (await api.Client.DeleteAsync(Uri(path + "/" + issued[0]))).EnsureSuccessStatusCode();
        while (next is not null)
        {
            var page = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{path}?limit=5&after={next}"));
            listed.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            next = page.GetProperty("next").GetString();
        }

        await Assert.That(string.Join(',', listed)).IsEqualTo(string.Join(',', issued));
        var remaining = await api.Client.GetFromJsonAsync<JsonElement>(Uri(path + "?limit=100"));
        await Assert.That(string.Join(',', remaining.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()))).IsEqualTo(string.Join(',', issued.Skip(1)));
    }

    [Test]
    public async Task A_project_key_is_created_listed_and_revoked()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var name = Faker.Hacker.Noun();

        using var created = await api.Client.PostAsJsonAsync(Uri($"{database.ProjectPath}/api-keys"), new { name, scopes = ReadWrite });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var secret = body.GetProperty("secret").GetString()!;
        var key = body.GetProperty("key");
        var id = key.GetProperty("id").GetString()!;
        var listed = await api.Client.GetStringAsync(Uri($"{database.ProjectPath}/api-keys"));
        using var list = JsonDocument.Parse(listed);
        var item = list.RootElement.GetProperty("items").EnumerateArray().Single();
        using var client = api.DataClient(secret);
        await ManagementApi.CreateCollectionAsync(client, database, "items");
        using var beforeRevoke = await client.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "items", "a")), new { n = 1 });
        using var revoked = await api.Client.DeleteAsync(Uri($"{database.ProjectPath}/api-keys/{id}"));
        using var afterRevoke = await client.GetAsync(Uri(ManagementApi.DocumentPath(database, "items", "a")));
        using var revokedAgain = await api.Client.DeleteAsync(Uri($"{database.ProjectPath}/api-keys/{id}"));
        using var emptyList = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{database.ProjectPath}/api-keys")));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{database.ProjectPath}/api-keys/{id}");
        await Assert.That(secret).StartsWith("tb_");
        await Assert.That(key.GetProperty("projectId").GetString()).IsEqualTo(database.Project);
        await Assert.That(key.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(key.GetProperty("databaseId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(key.GetProperty("lastFour").GetString()).IsEqualTo(secret[^4..]);
        await Assert.That(item.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(item.GetProperty("lastFour").GetString()).IsEqualTo(secret[^4..]);
        await Assert.That(item.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()!)).IsEquivalentTo(ReadWrite);
        await Assert.That(item.TryGetProperty("secret", out _) || item.TryGetProperty("hash", out _)).IsFalse();
        await Assert.That(listed).DoesNotContain(secret);
        await Assert.That(beforeRevoke.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterRevoke.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(revokedAgain.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(emptyList.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task A_project_key_reaches_only_its_project_databases()
    {
        await using var api = await ManagementApi.StartAsync();
        var own = await api.CreateManagedDatabaseAsync("main");
        var sibling = await api.CreateManagedDatabaseAsync(own.Organization, own.Project, "staging");
        var otherProject = await api.CreateManagedDatabaseAsync(own.Organization, await api.CreateProjectAsync(own.Organization, "Other"), "main");
        var foreign = await api.CreateManagedDatabaseAsync();
        using var project = api.DataClient(await CreateSecretAsync(api, own, ReadWrite));
        using var readOnly = api.DataClient(await CreateSecretAsync(api, own, ReadOnly));
        using var restricted = api.DataClient(await CreateSecretAsync(api, own, ReadWrite, own.Id));
        using var organizationKey = await api.CreateClientAsync(own.Organization, ReadWrite);
        using var foreignKey = await api.CreateClientAsync(foreign.Organization, ReadWrite);
        var document = new { title = Faker.Commerce.ProductName() };
        foreach (var database in new[] { own, sibling, otherProject })
        {
            await ManagementApi.CreateCollectionAsync(organizationKey, database, "items");
        }

        await ManagementApi.CreateCollectionAsync(foreignKey, foreign, "items");

        await Assert.That(await PutAsync(project, own, document)).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await GetAsync(project, own)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await PutAsync(project, sibling, document)).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await StatusAsync(project.PutAsync(Uri(own.Data), null))).IsEqualTo(HttpStatusCode.OK);
        // A database the key does not reach answers like one that does not exist (main's tenancy: 404).
        await Assert.That(await PutAsync(project, otherProject, document)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await PutAsync(project, foreign, document)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await GetAsync(project, foreign)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(project.GetAsync(Uri($"/v1/projects/{own.Project}/databases/nowhere/collections/items/documents/by-key?id={ManagementApi.DocumentId("a")}"))))
            .IsEqualTo(HttpStatusCode.NotFound);
        // A project key creates databases only in its project.
        await Assert.That(await StatusAsync(project.PutAsync(Uri($"/v1/projects/{own.Project}/databases/fresh"), null))).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await StatusAsync(project.PutAsync(Uri($"/v1/projects/{otherProject.Project}/databases/fresh"), null))).IsEqualTo(HttpStatusCode.NotFound);
        // The Management API takes only the dashboard credential: an API key there is not authenticated.
        await Assert.That(await StatusAsync(project.GetAsync(Uri("/v1/management/organizations")))).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await GetAsync(readOnly, own)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await PutAsync(readOnly, own, document)).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await PutAsync(restricted, own, document)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await GetAsync(restricted, sibling)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await PutAsync(restricted, sibling, document)).IsEqualTo(HttpStatusCode.NotFound);
        // A key of the whole organization reaches every project of it, and no other organization.
        await Assert.That(await PutAsync(organizationKey, otherProject, document)).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await GetAsync(organizationKey, own)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await PutAsync(organizationKey, foreign, document)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await PutAsync(foreignKey, foreign, document)).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Project_key_requests_answer_their_error_code_and_message()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var foreign = await api.CreateManagedDatabaseAsync();
        var keys = $"{database.ProjectPath}/api-keys";
        var unknownProject = $"/v1/management/organizations/{database.Organization}/projects/{EntityIds.New()}";
        const string BadName = "name must be 1 to 128 characters, include a letter or digit and have no control characters";
        const string BadScopes = "scopes must be a non-empty subset of: documents:read, documents:write";
        const string BadLimit = "limit must be an integer between 1 and 100";
        const string RoleTooLow = "your role in the organization does not allow this";

        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(keys), new { name = " ", scopes = ReadWrite }), HttpStatusCode.BadRequest, "invalid_request", BadName);
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri(keys), new { name = new string('k', 129), scopes = ReadWrite }), HttpStatusCode.BadRequest, "invalid_request", BadName);
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri(keys), new { name = "ci", scopes = Array.Empty<string>() }), HttpStatusCode.BadRequest, "invalid_request", BadScopes);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(keys), new { name = "ci" }), HttpStatusCode.BadRequest, "invalid_request", BadScopes);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(keys), new { name = "ci", scopes = ManagementScope }), HttpStatusCode.BadRequest, "invalid_request", BadScopes);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(keys), new { name = "ci", scopes = UnknownScope }), HttpStatusCode.BadRequest, "invalid_request", BadScopes);
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri(keys), new { name = "ci", scopes = ReadOnly, databaseId = foreign.Id }), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{EntityIds.New()}/projects/{database.Project}/api-keys"), new { name = "ci", scopes = ReadOnly }),
            HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{foreign.Organization}/projects/{database.Project}/api-keys"), new { name = "ci", scopes = ReadOnly }),
            HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{unknownProject}/api-keys")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{keys}?limit=0")), HttpStatusCode.BadRequest, "invalid_request", BadLimit);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{keys}?limit=ten")), HttpStatusCode.BadRequest, "invalid_request", BadLimit);
        await ErrorAssert.ErrorAsync(api.Client.DeleteAsync(Uri($"{keys}/{EntityIds.New()}")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.DeleteAsync(Uri($"{unknownProject}/api-keys/{EntityIds.New()}")), HttpStatusCode.NotFound, "not_found");

        // A viewer reads the project's keys but can neither issue nor revoke one.
        using var created = await api.Client.PostAsJsonAsync(Uri(keys), new { name = "ci", scopes = ReadOnly });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetProperty("id").GetString();
        using var viewer = api.As(await api.AddMemberAsync(api.Client, database.Organization, "viewer"));
        using var listed = await viewer.GetAsync(Uri(keys));

        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await ErrorAssert.ErrorAsync(viewer.PostAsJsonAsync(Uri(keys), new { name = "ci", scopes = ReadOnly }), HttpStatusCode.Forbidden, "forbidden_role", RoleTooLow);
        await ErrorAssert.ErrorAsync(viewer.DeleteAsync(Uri($"{keys}/{id}")), HttpStatusCode.Forbidden, "forbidden_role", RoleTooLow);
        await ErrorAssert.ErrorAsync(viewer.DeleteAsync(Uri($"{keys}/{EntityIds.New()}")), HttpStatusCode.NotFound, "not_found");
    }

    [Test]
    public async Task A_project_with_api_keys_cannot_be_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(Faker.Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, "Keys");
        var path = $"/v1/management/organizations/{organization}/projects/{project}";
        using var created = await api.Client.PostAsJsonAsync(Uri($"{path}/api-keys"), new { name = "ci", scopes = ReadOnly });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetProperty("id").GetString();

        using var refused = await api.Client.DeleteAsync(Uri(path));
        using var revoked = await api.Client.DeleteAsync(Uri($"{path}/api-keys/{id}"));
        using var deleted = await api.Client.DeleteAsync(Uri(path));

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task An_operator_issued_project_key_is_listed_revocable_and_keeps_its_project()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(Faker.Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, "Operator keys");
        var path = $"/v1/management/organizations/{organization}/projects/{project}";

        var (id, secret) = await IssueOperatorKeyAsync(api, organization, project);
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{path}/api-keys")));
        var item = list.RootElement.GetProperty("items").EnumerateArray().Single();
        using var refused = await api.Client.DeleteAsync(Uri(path));
        using var revoked = await api.Client.DeleteAsync(Uri($"{path}/api-keys/{id}"));
        using var client = api.DataClient(secret);
        using var afterRevoke = await client.GetAsync(Uri($"/v1/projects/{project}/databases/main/collections"));
        using var deleted = await api.Client.DeleteAsync(Uri(path));

        await Assert.That(item.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(item.GetProperty("lastFour").GetString()).IsEqualTo(secret[^4..]);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterRevoke.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Operator_project_keys_remain_listed_and_revocable_after_restart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-key-listing-").FullName;
        try
        {
            string user, project, path, id, secret;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                user = api.User;
                var organization = await api.CreateOrganizationAsync(Faker.Company.CompanyName());
                project = await api.CreateProjectAsync(organization, "Operator keys");
                path = $"/v1/management/organizations/{organization}/projects/{project}";
                (id, secret) = await IssueOperatorKeyAsync(api, organization, project);
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                using var owner = api.As(user);
                using var list = JsonDocument.Parse(await owner.GetStringAsync(Uri($"{path}/api-keys")));
                var item = list.RootElement.GetProperty("items").EnumerateArray().Single();
                using var refused = await owner.DeleteAsync(Uri(path));
                using var revoked = await owner.DeleteAsync(Uri($"{path}/api-keys/{id}"));
                using var client = api.DataClient(secret);
                using var afterRevoke = await client.GetAsync(Uri($"/v1/projects/{project}/databases/main/collections"));
                using var deleted = await owner.DeleteAsync(Uri(path));

                await Assert.That(item.GetProperty("id").GetString()).IsEqualTo(id);
                await Assert.That(item.GetProperty("lastFour").GetString()).IsEqualTo(secret[^4..]);
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                await Assert.That(afterRevoke.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Issues a project key through the administrative listener, as an operator does.
    private static async Task<(string Id, string Secret)> IssueOperatorKeyAsync(ManagementApi api, string organization, string project)
    {
        using var issued = await api.Server.Client.PostAsJsonAsync(
            Uri("/admin/api-keys"), new { name = Faker.Hacker.Noun(), scopes = ReadOnly, organizationId = organization, projectId = project });
        issued.EnsureSuccessStatusCode();
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!);
    }

    private static async Task<string> CreateSecretAsync(ManagementApi api, ManagedDatabaseRef database, string[] scopes, string? databaseId = null)
    {
        using var response = await api.Client.PostAsJsonAsync(Uri($"{database.ProjectPath}/api-keys"), new { name = Faker.Hacker.Noun(), scopes, databaseId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
    }

    private static Task<HttpStatusCode> PutAsync(HttpClient client, ManagedDatabaseRef database, object document) =>
        StatusAsync(client.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "items", "doc")), document));

    private static Task<HttpStatusCode> GetAsync(HttpClient client, ManagedDatabaseRef database) =>
        StatusAsync(client.GetAsync(Uri(ManagementApi.DocumentPath(database, "items", "doc"))));

    private static Uri Uri(string path) => ManagementApi.Uri(path);

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }
}
