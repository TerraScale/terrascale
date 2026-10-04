using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

public sealed class ManagementApiTests
{
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string OrganizationHeader = "X-TerraBase-Organization-Id";

    private static readonly Faker Faker = new();

    [Test]
    public async Task Organizations_are_created_listed_renamed_and_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var name = Faker.Company.CompanyName();

        using var created = await api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name });
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = organization.GetProperty("id").GetString()!;
        using var read = await api.Client.GetAsync(Uri($"/v1/management/organizations/{id}"));
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations")));
        using var renamed = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{id}"), new { name = "Renamed Org" });
        var afterRename = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{id}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{id}"));
        using var gone = await api.Client.GetAsync(Uri($"/v1/management/organizations/{id}"));
        using var listAfterDelete = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations")));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"/v1/management/organizations/{id}");
        await Assert.That(organization.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(organization.GetProperty("createdBy").GetString()).IsEqualTo(api.User);
        await Assert.That(Guid.TryParse(id, out _)).IsTrue();
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(list.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([id]);
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(afterRename.GetProperty("name").GetString()).IsEqualTo("Renamed Org");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(listAfterDelete.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task A_user_lists_only_their_organizations_in_pages()
    {
        await using var api = await ManagementApi.StartAsync();
        using var other = api.As("other-user");
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await api.CreateOrganizationAsync($"Org {i}"));
            await api.CreateOrganizationAsync($"Org {i}", other);
        }

        using var first = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations?limit=2")));
        var next = first.RootElement.GetProperty("next").GetString();
        using var second = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"/v1/management/organizations?limit=2&after={next}")));

        string[] seen =
        [
            .. first.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!),
            .. second.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!),
        ];
        await Assert.That(seen).IsEquivalentTo(ids);
        await Assert.That(seen[0]).IsEqualTo(ids[0]);
        await Assert.That(second.RootElement.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task A_user_switches_between_their_own_organizations()
    {
        await using var api = await ManagementApi.StartAsync();
        using var other = api.As("other-user");
        var first = await api.CreateOrganizationAsync("First");
        var second = await api.CreateOrganizationAsync("Second");
        var foreign = await api.CreateOrganizationAsync("Foreign", other);
        const string current = "/v1/management/current-organization";

        using var none = await api.Client.GetAsync(Uri(current));
        using var switchedFirst = await api.Client.PutAsJsonAsync(Uri(current), new { organizationId = first });
        using var switchedSecond = await api.Client.PutAsJsonAsync(Uri(current), new { organizationId = second });
        var selected = await api.Client.GetFromJsonAsync<JsonElement>(Uri(current));
        using var otherUserNone = await other.GetAsync(Uri(current));
        using var notMember = await api.Client.PutAsJsonAsync(Uri(current), new { organizationId = foreign });
        using var unknown = await api.Client.PutAsJsonAsync(Uri(current), new { organizationId = Guid.NewGuid() });
        using var missing = await api.Client.PutAsJsonAsync(Uri(current), new { });
        (await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{second}"))).Dispose();
        using var afterDelete = await api.Client.GetAsync(Uri(current));

        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(switchedFirst.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(switchedSecond.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(selected.GetProperty("id").GetString()).IsEqualTo(second);
        await Assert.That(otherUserNone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(notMember.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(afterDelete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Projects_are_created_listed_renamed_and_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var other = await api.CreateOrganizationAsync("Other");
        var projects = $"/v1/management/organizations/{organization}/projects";

        using var created = await api.Client.PostAsJsonAsync(Uri(projects), new { name = "Production" });
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(projects)));
        using var renamed = await api.Client.PatchAsJsonAsync(Uri($"{projects}/{project}"), new { name = "Staging" });
        var read = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{projects}/{project}"));
        using var underOtherOrganization = await api.Client.GetAsync(Uri($"/v1/management/organizations/{other}/projects/{project}"));
        using var organizationNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{projects}/{project}"));
        using var gone = await api.Client.GetAsync(Uri($"{projects}/{project}"));
        using var organizationEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{projects}/{project}");
        await Assert.That(list.RootElement.GetProperty("items")[0].GetProperty("organizationId").GetString()).IsEqualTo(organization);
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.GetProperty("name").GetString()).IsEqualTo("Staging");
        await Assert.That(underOtherOrganization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(organizationNotEmpty.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(organizationEmpty.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Databases_are_created_branched_and_deleted_with_their_data()
    {
        await using var api = await ManagementApi.StartAsync();
        using var data = api.Tls.CreateClient(api.Server.ClientAddress!, await api.Server.IssueAsync("documents:read", "documents:write"));
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Shop");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        var person = new { name = Faker.Name.FullName() };

        using var created = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var main = await created.Content.ReadFromJsonAsync<JsonElement>();
        var mainId = main.GetProperty("id").GetString()!;
        var mainStorage = main.GetProperty("storageName").GetString()!;
        (await data.PutAsJsonAsync(Uri($"/v1/databases/{mainStorage}/collections/people/documents/p1"), person)).EnsureSuccessStatusCode();
        using var branched = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "preview" });
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>();
        var branchStorage = branch.GetProperty("storageName").GetString()!;
        (await data.PutAsJsonAsync(Uri($"/v1/databases/{branchStorage}/collections/people/documents/p2"), person)).EnsureSuccessStatusCode();
        using var mainWithoutBranchWrite = await data.GetAsync(Uri($"/v1/databases/{mainStorage}/collections/people/documents/p2"));
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));
        using var projectNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{databases}/{mainId}"));
        using var gone = await api.Client.GetAsync(Uri($"{databases}/{mainId}"));
        using var dataGone = await data.GetAsync(Uri($"/v1/databases/{mainStorage}/collections/people/documents/p1"));
        using var branchKeepsCopy = await data.GetAsync(Uri($"/v1/databases/{branchStorage}/collections/people/documents/p1"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(main.GetProperty("name").GetString()).IsEqualTo("main");
        await Assert.That(main.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(branch.GetProperty("branchedFrom").GetString()).IsEqualTo(mainId);
        await Assert.That(mainWithoutBranchWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(projectNotEmpty.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(dataGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, mainStorage + ".db"))).IsFalse();
        await Assert.That(branchKeepsCopy.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Duplicate_names_conflict_ignoring_case_within_the_same_parent()
    {
        await using var api = await ManagementApi.StartAsync();
        using var otherUser = api.As("other-user");
        var acme = await api.CreateOrganizationAsync("Acme");
        var globex = await api.CreateOrganizationAsync("Globex");
        var project = await api.CreateProjectAsync(acme, "Web");
        await api.CreateProjectAsync(acme, "Mobile");
        var databases = $"/v1/management/organizations/{acme}/projects/{project}/databases";
        using var main = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var mainId = (await main.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        using var organization = await api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "ACME" });
        using var organizationRename = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{globex}"), new { name = "acme" });
        using var organizationOwnCase = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{acme}"), new { name = "ACME" });
        using var organizationOtherUser = await otherUser.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "Acme" });
        using var projectDuplicate = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{acme}/projects"), new { name = "web" });
        using var projectRename = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{acme}/projects/{project}"), new { name = "MOBILE" });
        using var projectElsewhere = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{globex}/projects"), new { name = "Web" });
        using var databaseDuplicate = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        using var branchDuplicate = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "main" });
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));

        await Assert.That(organization.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(organizationRename.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(organizationOwnCase.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // Organization names are unique per creator, so they reveal nothing about other tenants.
        await Assert.That(organizationOtherUser.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(projectDuplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(projectRename.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(projectElsewhere.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(databaseDuplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(branchDuplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        // A rejected create or branch leaves no database behind.
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Directory.EnumerateFiles(api.Server.DataDirectory, "db-*").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Unknown_resources_are_not_found()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        var unknown = Guid.NewGuid();

        var statuses = await AllStatusesAsync(api.Client, $"/v1/management/organizations/{unknown}", unknown.ToString(), unknown.ToString());
        statuses.AddRange(await AllStatusesAsync(api.Client, $"/v1/management/organizations/{organization}", unknown.ToString(), unknown.ToString(), Level.Project));
        statuses.AddRange(await AllStatusesAsync(api.Client, $"/v1/management/organizations/{organization}", project, unknown.ToString(), Level.Database));

        await Assert.That(statuses).Count().IsEqualTo(13 + 8 + 3);
        await Assert.That(statuses.Distinct()).IsEquivalentTo([HttpStatusCode.NotFound]);
    }

    [Test]
    public async Task Users_cannot_reach_organizations_they_are_not_members_of()
    {
        await using var api = await ManagementApi.StartAsync();
        using var intruder = api.As("intruder");
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        using var created = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main" });
        var database = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var statuses = await AllStatusesAsync(intruder, $"/v1/management/organizations/{organization}", project, database);
        await Assert.That(statuses).Count().IsEqualTo(13);
        using var intruderList = JsonDocument.Parse(await intruder.GetStringAsync(Uri("/v1/management/organizations")));
        using var stillThere = await api.Client.GetAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}/databases/{database}"));
        var unchanged = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));

        // 404, not 403: a non-member cannot even confirm that the organization exists.
        await Assert.That(statuses.Distinct()).IsEquivalentTo([HttpStatusCode.NotFound]);
        await Assert.That(intruderList.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(stillThere.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unchanged.GetProperty("name").GetString()).IsEqualTo("Acme");
    }

    [Test]
    public async Task Invalid_names_queries_and_identity_headers_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        var other = await api.CreateOrganizationAsync("Other");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        object[] badDisplayNames = [new { }, new { name = "" }, new { name = "   " }, new { name = "--" }, new { name = new string('a', 129) }, new { name = "a\nb" }];
        object[] badDatabaseNames = [new { }, new { name = "Main" }, new { name = "1db" }, new { name = "con" }, new { name = new string('a', 64) }];

        var statuses = new List<HttpStatusCode>();
        foreach (var body in badDisplayNames)
        {
            statuses.Add(await StatusAsync(api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), body)));
            statuses.Add(await StatusAsync(api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{organization}"), body)));
            statuses.Add(await StatusAsync(api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects"), body)));
            statuses.Add(await StatusAsync(api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"), body)));
        }

        foreach (var body in badDatabaseNames)
        {
            statuses.Add(await StatusAsync(api.Client.PostAsJsonAsync(Uri(databases), body)));
        }

        statuses.Add(await StatusAsync(api.Client.GetAsync(Uri("/v1/management/organizations?limit=0"))));
        statuses.Add(await StatusAsync(api.Client.GetAsync(Uri("/v1/management/organizations?limit=101"))));
        statuses.Add(await StatusAsync(api.Client.PostAsync(Uri("/v1/management/organizations"), new StringContent("{oops", null, "application/json"))));
        // Identity headers: the user is required and single-valued; the organization header must match the path.
        statuses.Add(await api.SendAsync(HttpMethod.Get, "/v1/management/organizations", headers => headers.Remove(UserHeader)));
        statuses.Add(await api.SendAsync(HttpMethod.Get, "/v1/management/organizations", headers =>
        {
            headers.Remove(UserHeader);
            headers.Add(UserHeader, new string('u', 257));
        }));
        statuses.Add(await api.SendAsync(HttpMethod.Get, "/v1/management/organizations", headers => headers.Add(UserHeader, "bad\u0001user")));
        statuses.Add(await api.SendAsync(HttpMethod.Get, $"/v1/management/organizations/{organization}", headers => headers.Remove(OrganizationHeader)));
        statuses.Add(await api.SendAsync(HttpMethod.Get, $"/v1/management/organizations/{organization}", headers => headers.Add(OrganizationHeader, other)));
        statuses.Add(await api.SendAsync(HttpMethod.Get, $"/v1/management/organizations/{organization}/projects", headers =>
        {
            headers.Remove(OrganizationHeader);
            headers.Add(OrganizationHeader, other);
        }));

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.BadRequest));
        // Names are trimmed rather than rejected for surrounding spaces.
        var trimmed = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{await api.CreateOrganizationAsync("  Spaced  ")}"));
        await Assert.That(trimmed.GetProperty("name").GetString()).IsEqualTo("Spaced");
    }

    [Test]
    public async Task Management_needs_the_dashboard_credential_on_the_client_listener()
    {
        await using var api = await ManagementApi.StartAsync();
        using var anonymous = api.Tls.CreateClient(api.Server.ClientAddress!);
        using var wrongCredential = api.Tls.CreateClient(api.Server.ClientAddress!, ManagementApi.Credential + "x");
        using var apiKey = api.Tls.CreateClient(api.Server.ClientAddress!, await api.Server.IssueAsync("documents:read", "documents:write"));
        using var dashboard = api.Tls.CreateClient(api.Server.ClientAddress!, ManagementApi.Credential);
        foreach (var client in new[] { anonymous, wrongCredential, apiKey })
        {
            client.DefaultRequestHeaders.Add(UserHeader, api.User);
        }

        using var noCredential = await anonymous.GetAsync(Uri("/v1/management/organizations"));
        using var wrong = await wrongCredential.GetAsync(Uri("/v1/management/organizations"));
        using var withApiKey = await apiKey.GetAsync(Uri("/v1/management/organizations"));
        using var onAdmin = await api.Server.Client.GetAsync(Uri("/v1/management/organizations"));
        using var noDocumentAccess = await dashboard.PutAsync(Uri("/v1/databases/x"), null);

        await Assert.That(noCredential.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withApiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(noDocumentAccess.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Management_is_disabled_without_a_configured_credential()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, ManagementApi.Credential);
        client.DefaultRequestHeaders.Add(UserHeader, "user");

        using var response = await client.GetAsync(Uri("/v1/management/organizations"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_catalog_from_before_membership_is_migrated_to_the_configured_owner()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-legacy-").FullName;
        try
        {
            var (organization, project) = await WriteVersionOneCatalogAsync(dataDirectory);
            const string owner = "legacy-owner";

            HttpStatusCode unmigrated;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory))
            {
                using var response = await api.Client.GetAsync(Uri("/v1/management/organizations"));
                unmigrated = response.StatusCode;
            }

            HttpStatusCode ownerProject, outsider;
            string[] owned;
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                settings["data-directory"] = dataDirectory;
                settings["management-legacy-owner"] = owner;
            }))
            {
                using var ownerClient = api.As(owner);
                using var outsiderClient = api.As("someone-else");
                using var list = JsonDocument.Parse(await ownerClient.GetStringAsync(Uri("/v1/management/organizations")));
                owned = [.. list.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("createdBy").GetString()!)];
                ownerProject = await StatusAsync(ownerClient.GetAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}")));
                outsider = await StatusAsync(outsiderClient.GetAsync(Uri($"/v1/management/organizations/{organization}")));
                // The global name claim moved to the owner's own index.
                await Assert.That(await StatusAsync(ownerClient.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "ACME" })))
                    .IsEqualTo(HttpStatusCode.Conflict);
                await Assert.That(await StatusAsync(outsiderClient.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "Acme" })))
                    .IsEqualTo(HttpStatusCode.Created);
            }

            // The migration is recorded: later starts need no setting.
            HttpStatusCode afterRestart;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory))
            {
                using var ownerClient = api.As(owner);
                afterRestart = await StatusAsync(ownerClient.GetAsync(Uri($"/v1/management/organizations/{organization}")));
            }

            await Assert.That(unmigrated).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(owned).IsEquivalentTo([owner]);
            await Assert.That(ownerProject).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(outsider).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(afterRestart).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_catalog_with_an_unsupported_version_is_refused_and_left_unchanged()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-future-").FullName;
        try
        {
            var organization = Guid.CreateVersion7().ToString();
            await WriteSystemDocumentsAsync(
                dataDirectory,
                ("catalog", "version", JsonSerializer.Serialize("3")),
                ("organizations", organization, $$"""{"id":"{{organization}}","name":"Acme","createdBy":"alice","createdAt":"2026-10-04T12:00:00+00:00"}"""),
                ($"organizations/{organization}/members", "alice", JsonSerializer.Serialize("alice")),
                ("users/alice/organizations", organization, JsonSerializer.Serialize(organization)),
                ("users/alice/organization_names", "acme", JsonSerializer.Serialize(organization)));
            var before = await ReadSystemDocumentsAsync(dataDirectory);

            HttpStatusCode list, create;
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                settings["data-directory"] = dataDirectory;
                settings["management-legacy-owner"] = "mallory";
            }))
            {
                using var mallory = api.As("mallory");
                list = await StatusAsync(mallory.GetAsync(Uri("/v1/management/organizations")));
                create = await StatusAsync(mallory.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "Other" }));
            }

            SqliteConnection.ClearAllPools();
            await Assert.That(list).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(create).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            // Ownership, memberships, name claims and the version marker are exactly as written.
            await Assert.That(await ReadSystemDocumentsAsync(dataDirectory)).IsEquivalentTo(before);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    // The _system database exactly as the first Management release wrote it: no members, no catalog version.
    private static async Task<(string Organization, string Project)> WriteVersionOneCatalogAsync(string dataDirectory)
    {
        var organization = Guid.CreateVersion7().ToString();
        var project = Guid.CreateVersion7().ToString();
        await WriteSystemDocumentsAsync(
            dataDirectory,
            ("organizations", organization, $$"""{"id":"{{organization}}","name":"Acme","createdAt":"2026-10-04T12:00:00+00:00"}"""),
            ("organization_names", "acme", JsonSerializer.Serialize(organization)),
            ($"organizations/{organization}/projects", project,
                $$"""{"id":"{{project}}","organizationId":"{{organization}}","name":"Web","createdAt":"2026-10-04T12:00:00+00:00"}"""),
            ($"organizations/{organization}/project_names", "web", JsonSerializer.Serialize(project)));
        return (organization, project);
    }

    // Writes a schema-version-1 _system database holding exactly these documents.
    private static async Task WriteSystemDocumentsAsync(string dataDirectory, params (string Collection, string Id, string Body)[] documents)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "_system.db")};Pooling=False");
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY (collection, id)) WITHOUT ROWID;
                PRAGMA user_version = 1;
                """;
            await schema.ExecuteNonQueryAsync();
        }

        foreach (var (collection, id, body) in documents)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO documents VALUES ($collection, $id, 1, $body);";
            insert.Parameters.AddWithValue("$collection", collection);
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$body", body);
            await insert.ExecuteNonQueryAsync();
        }
    }

    // Every document of the _system database, as "collection|id|body" lines in key order.
    private static async Task<List<string>> ReadSystemDocumentsAsync(string dataDirectory)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "_system.db")};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection, id, body FROM documents ORDER BY collection, id;";
        await using var reader = await command.ExecuteReaderAsync();
        var documents = new List<string>();
        while (await reader.ReadAsync())
        {
            documents.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        }

        return documents;
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    // Every route under one organization from `from` down, addressing the given project and database ids.
    private static async Task<List<HttpStatusCode>> AllStatusesAsync(
        HttpClient client, string organization, string project, string database, Level from = Level.Organization)
    {
        var named = new { name = "valid" };
        var projectPath = $"{organization}/projects/{project}";
        var databasePath = $"{projectPath}/databases/{database}";
        (Level Level, Func<Task<HttpResponseMessage>> Send)[] requests =
        [
            (Level.Organization, () => client.GetAsync(Uri(organization))),
            (Level.Organization, () => client.PatchAsJsonAsync(Uri(organization), named)),
            (Level.Organization, () => client.DeleteAsync(Uri(organization))),
            (Level.Organization, () => client.GetAsync(Uri($"{organization}/projects"))),
            (Level.Organization, () => client.PostAsJsonAsync(Uri($"{organization}/projects"), named)),
            (Level.Project, () => client.GetAsync(Uri(projectPath))),
            (Level.Project, () => client.PatchAsJsonAsync(Uri(projectPath), named)),
            (Level.Project, () => client.DeleteAsync(Uri(projectPath))),
            (Level.Project, () => client.GetAsync(Uri($"{projectPath}/databases"))),
            (Level.Project, () => client.PostAsJsonAsync(Uri($"{projectPath}/databases"), named)),
            (Level.Database, () => client.GetAsync(Uri(databasePath))),
            (Level.Database, () => client.DeleteAsync(Uri(databasePath))),
            (Level.Database, () => client.PostAsJsonAsync(Uri($"{databasePath}/branches"), named)),
        ];
        var statuses = new List<HttpStatusCode>();
        foreach (var (level, send) in requests.Where(request => request.Level >= from))
        {
            statuses.Add(await StatusAsync(send()));
        }

        return statuses;
    }

    private enum Level
    {
        Organization,
        Project,
        Database,
    }

    /// <summary>
    /// A server with the client listener and the dashboard credential, and clients that send it like
    /// ts-dashboard: the acting user on every request and, on organization routes, the organization from the path.
    /// </summary>
    private sealed class ManagementApi : IAsyncDisposable
    {
        public const string Credential = "test-dashboard-credential-0123456789abcdef";

        private readonly string _directory;

        private ManagementApi(TerraBaseTestServer server, ClientTls tls, string directory, string user)
        {
            Server = server;
            Tls = tls;
            _directory = directory;
            User = user;
            Client = As(user);
        }

        public TerraBaseTestServer Server { get; }

        public ClientTls Tls { get; }

        public string User { get; }

        public HttpClient Client { get; }

        public static async Task<ManagementApi> StartAsync(Action<Dictionary<string, string>>? configure = null)
        {
            var directory = Directory.CreateTempSubdirectory("terrabase-dashboard-").FullName;
            var credentialFile = Path.Combine(directory, "credential");
            await File.WriteAllTextAsync(credentialFile, Credential + "\n");
            var tls = new ClientTls();
            var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["dashboard-credential-file"] = credentialFile;
                configure?.Invoke(settings);
            });
            return new ManagementApi(server, tls, directory, Faker.Random.Guid().ToString());
        }

        public HttpClient As(string user)
        {
            var client = new HttpClient(new DashboardHeaders(user) { InnerHandler = Tls.CreateHandler() })
            {
                BaseAddress = Server.ClientAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
            return client;
        }

        /// <summary>Sends a request after <paramref name="edit"/> changes its final headers.</summary>
        public async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, Action<HttpRequestHeaders> edit)
        {
            using var client = new HttpClient(new DashboardHeaders(User, edit) { InnerHandler = Tls.CreateHandler() }) { BaseAddress = Server.ClientAddress };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
            using var request = new HttpRequestMessage(method, Uri(path));
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        public async Task<string> CreateOrganizationAsync(string name, HttpClient? client = null)
        {
            using var response = await (client ?? Client).PostAsJsonAsync(Uri("/v1/management/organizations"), new { name });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        public async Task<string> CreateProjectAsync(string organization, string name)
        {
            using var response = await Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects"), new { name });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Tls.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class DashboardHeaders(string user, Action<HttpRequestHeaders>? edit = null) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(UserHeader, user);
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            if (segments.Length > 4 && segments[3] == "organizations")
            {
                request.Headers.Add(OrganizationHeader, segments[4]);
            }

            edit?.Invoke(request.Headers);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
