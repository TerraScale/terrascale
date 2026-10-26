using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ManagementApiTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] ReadScopes = ["documents:read"];

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
        await Assert.That(organization.GetProperty("plan").GetString()).IsEqualTo("hobby");
        await Assert.That(EntityIds.IsValid(id)).IsTrue();
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(list.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([id]);
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(afterRename.GetProperty("name").GetString()).IsEqualTo("Renamed Org");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(listAfterDelete.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task An_organization_plan_defaults_to_hobby_and_needs_the_service_credential_acting_as_owner()
    {
        await using var api = await ManagementApi.StartAsync();
        using var other = api.As(EntityIds.New().ToString());
        var organization = await api.CreateOrganizationAsync("Acme");
        var plan = $"/v1/management/organizations/{organization}/plan";

        var initial = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));
        using var service = api.Service(api.User);
        using var byOwner = await api.Client.PutAsJsonAsync(Uri(plan), new { plan = "enterprise" });
        using var pro = await service.PutAsJsonAsync(Uri(plan), new { plan = "pro" });
        using var enterprise = await service.PutAsJsonAsync(Uri(plan), new { plan = "enterprise" });
        var read = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));
        using var listed = JsonDocument.Parse(await api.Client.GetStringAsync(Uri("/v1/management/organizations")));
        using var nonMember = await other.PutAsJsonAsync(Uri(plan), new { plan = "hobby" });
        using var admin = api.As(await api.AddMemberAsync(api.Client, organization, "admin"));
        using var member = api.As(await api.AddMemberAsync(api.Client, organization, "member"));
        using var byAdmin = await admin.PutAsJsonAsync(Uri(plan), new { plan = "hobby" });
        using var byMember = await member.PutAsJsonAsync(Uri(plan), new { plan = "hobby" });
        var afterRefused = await member.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));

        await Assert.That(initial.GetProperty("plan").GetString()).IsEqualTo("hobby");
        await Assert.That(pro.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await pro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetString()).IsEqualTo("pro");
        await Assert.That(enterprise.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.GetProperty("plan").GetString()).IsEqualTo("enterprise");
        await Assert.That(listed.RootElement.GetProperty("items")[0].GetProperty("plan").GetString()).IsEqualTo("enterprise");
        await Assert.That(nonMember.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // User tokens cannot grant a resource entitlement, regardless of organization role.
        await ErrorAssert.ErrorAsync(byOwner, HttpStatusCode.Forbidden, "forbidden", "plan changes need the platform operator");
        await ErrorAssert.ErrorAsync(byAdmin, HttpStatusCode.Forbidden, "forbidden", "plan changes need the platform operator");
        await ErrorAssert.ErrorAsync(byMember, HttpStatusCode.Forbidden, "forbidden", "plan changes need the platform operator");
        await Assert.That(afterRefused.GetProperty("plan").GetString()).IsEqualTo("enterprise");
    }

    [Test]
    public async Task A_user_lists_only_their_organizations_in_pages()
    {
        await using var api = await ManagementApi.StartAsync();
        using var other = api.As(EntityIds.New().ToString());
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
        using var other = api.As(EntityIds.New().ToString());
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
        using var unknown = await api.Client.PutAsJsonAsync(Uri(current), new { organizationId = EntityIds.New().ToString() });
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
        await ErrorAssert.ErrorAsync(organizationNotEmpty, HttpStatusCode.Conflict, "not_empty", "delete what it contains first");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(organizationEmpty.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Databases_are_created_branched_and_deleted_with_their_data()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Shop");
        using var data = api.DataClient(await api.Server.IssueAsync(organization, "documents:read", "documents:write"));
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        var dataApi = $"/v1/projects/{project}/databases";
        var person = new { name = Faker.Name.FullName() };
        var (p1, p2) = (EntityIds.New().ToString(), EntityIds.New().ToString());

        using var created = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        var main = await created.Content.ReadFromJsonAsync<JsonElement>();
        var mainId = main.GetProperty("id").GetString()!;
        (await data.PostAsJsonAsync(Uri($"{dataApi}/main/collections"), new { name = "people" })).EnsureSuccessStatusCode();
        (await data.PutAsJsonAsync(Uri($"{dataApi}/main/collections/people/documents/by-key?id={p1}"), person)).EnsureSuccessStatusCode();
        using var branched = await api.Client.PostAsJsonAsync(Uri($"{databases}/{mainId}/branches"), new { name = "preview" });
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>();
        (await data.PutAsJsonAsync(Uri($"{dataApi}/preview/collections/people/documents/by-key?id={p2}"), person)).EnsureSuccessStatusCode();
        using var mainWithoutBranchWrite = await data.GetAsync(Uri($"{dataApi}/main/collections/people/documents/by-key?id={p2}"));
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));
        using var projectNotEmpty = await api.Client.DeleteAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}"));
        using var deleted = await api.Client.DeleteAsync(Uri($"{databases}/{mainId}"));
        using var gone = await api.Client.GetAsync(Uri($"{databases}/{mainId}"));
        using var dataGone = await data.GetAsync(Uri($"{dataApi}/main/collections/people/documents/by-key?id={p1}"));
        using var branchKeepsCopy = await data.GetAsync(Uri($"{dataApi}/preview/collections/people/documents/by-key?id={p1}"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(main.GetProperty("name").GetString()).IsEqualTo("main");
        await Assert.That(main.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That(EntityIds.IsValid(mainId)).IsTrue();
        await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(EntityIds.IsValid(branch.GetProperty("id").GetString())).IsTrue();
        await Assert.That(branch.GetProperty("branchedFrom").GetString()).IsEqualTo(mainId);
        await Assert.That(mainWithoutBranchWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await ErrorAssert.ErrorAsync(projectNotEmpty, HttpStatusCode.Conflict, "not_empty", "delete what it contains first");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(dataGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, organization, project, mainId + ".db"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, organization, project, branch.GetProperty("id").GetString() + ".db"))).IsTrue();
        await Assert.That(branchKeepsCopy.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Duplicate_names_conflict_ignoring_case_within_the_same_parent()
    {
        await using var api = await ManagementApi.StartAsync();
        using var otherUser = api.As(EntityIds.New().ToString());
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

        await ErrorAssert.ErrorAsync(organization, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await ErrorAssert.ErrorAsync(organizationRename, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await Assert.That(organizationOwnCase.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // Organization names are unique per creator, so they reveal nothing about other tenants.
        await Assert.That(organizationOtherUser.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await ErrorAssert.ErrorAsync(projectDuplicate, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await ErrorAssert.ErrorAsync(projectRename, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await Assert.That(projectElsewhere.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await ErrorAssert.ErrorAsync(databaseDuplicate, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await ErrorAssert.ErrorAsync(branchDuplicate, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        // A rejected create or branch leaves no database behind.
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        // Database files only: an open database also has Turso -wal and -shm files.
        await Assert.That(Directory.EnumerateFiles(Path.Combine(api.Server.DataDirectory, acme, project), "*.db").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Unknown_resources_are_not_found()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        var unknown = EntityIds.New().ToString();

        var statuses = await AllStatusesAsync(api.Client, $"/v1/management/organizations/{unknown}", unknown.ToString(), unknown.ToString());
        statuses.AddRange(await AllStatusesAsync(api.Client, $"/v1/management/organizations/{organization}", unknown.ToString(), unknown.ToString(), Level.Project));
        statuses.AddRange(await AllStatusesAsync(api.Client, $"/v1/management/organizations/{organization}", project, unknown.ToString(), Level.Database));

        await Assert.That(statuses).Count().IsEqualTo(14 + 8 + 3);
        await Assert.That(statuses.Distinct()).IsEquivalentTo([HttpStatusCode.NotFound]);
    }

    [Test]
    public async Task Users_cannot_reach_organizations_they_are_not_members_of()
    {
        await using var api = await ManagementApi.StartAsync();
        using var intruder = api.As(EntityIds.New().ToString());
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
        using var created = await api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main" });
        var database = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var statuses = await AllStatusesAsync(intruder, $"/v1/management/organizations/{organization}", project, database);
        await Assert.That(statuses).Count().IsEqualTo(14);
        using var intruderList = JsonDocument.Parse(await intruder.GetStringAsync(Uri("/v1/management/organizations")));
        using var stillThere = await api.Client.GetAsync(Uri($"/v1/management/organizations/{organization}/projects/{project}/databases/{database}"));
        var unchanged = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{organization}"));

        // 404, not 403: a non-member cannot even confirm that the organization exists.
        await Assert.That(statuses.Distinct()).IsEquivalentTo([HttpStatusCode.NotFound]);
        await Assert.That(intruderList.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(stillThere.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unchanged.GetProperty("name").GetString()).IsEqualTo("Acme");
        await Assert.That(unchanged.GetProperty("plan").GetString()).IsEqualTo("hobby");
    }

    [Test]
    public async Task Invalid_names_and_queries_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Web");
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

        foreach (var body in new object[] { new { }, new { plan = "Pro" }, new { plan = "free" }, new { plan = "1" } })
        {
            statuses.Add(await StatusAsync(api.Client.PutAsJsonAsync(Uri($"/v1/management/organizations/{organization}/plan"), body)));
        }

        statuses.Add(await StatusAsync(api.Client.PutAsync(Uri($"/v1/management/organizations/{organization}/plan"), new StringContent("""{"plan":1}""", null, "application/json"))));
        statuses.Add(await StatusAsync(api.Client.GetAsync(Uri("/v1/management/organizations?limit=0"))));
        statuses.Add(await StatusAsync(api.Client.GetAsync(Uri("/v1/management/organizations?limit=101"))));
        statuses.Add(await StatusAsync(api.Client.PostAsync(Uri("/v1/management/organizations"), new StringContent("{oops", null, "application/json"))));

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.BadRequest));
        // Names are trimmed rather than rejected for surrounding spaces.
        var trimmed = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{await api.CreateOrganizationAsync("  Spaced  ")}"));
        await Assert.That(trimmed.GetProperty("name").GetString()).IsEqualTo("Spaced");
    }

    [Test]
    public async Task Management_needs_a_user_token_on_the_client_listener()
    {
        await using var api = await ManagementApi.StartAsync();
        using var anonymous = api.WithToken(null);
        using var garbage = api.WithToken(Faker.Random.AlphaNumeric(40));
        using var apiKey = api.DataClient(await api.Server.IssueAsync(await api.CreateOrganizationAsync("Acme"), "documents:read", "documents:write"));
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, Uri("/v1/management/organizations"));
        adminRequest.Headers.Authorization = new("Bearer", api.Issuer.Mint(api.User));

        using var noToken = await anonymous.GetAsync(Uri("/v1/management/organizations"));
        using var notAToken = await garbage.GetAsync(Uri("/v1/management/organizations"));
        using var withApiKey = await apiKey.GetAsync(Uri("/v1/management/organizations"));
        using var onAdmin = await api.Server.Client.SendAsync(adminRequest);
        using var noDocumentAccess = await api.Client.PutAsync(Uri($"/v1/projects/{EntityIds.New()}/databases/x"), null);

        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(notAToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withApiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // The internal listener takes only the platform operator, never a ts-auth token.
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // A sign-in token without project_id does not reach a data project.
        await Assert.That(noDocumentAccess.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Management_is_disabled_without_an_auth_issuer()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, issuer.Route);
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        using var response = await client.GetAsync(Uri("/v1/management/organizations"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("2")]
    [Arguments("5")]
    [Arguments("99")]
    public async Task Noncurrent_catalog_versions_are_refused_and_left_unchanged(string version)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-catalog-format-").FullName;
        try
        {
            await WriteSystemDocumentsAsync(directory, ("catalog", "version", JsonSerializer.Serialize(version)));
            var before = await ReadSystemDocumentsAsync(directory);
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri("/v1/management/organizations")),
                    HttpStatusCode.ServiceUnavailable, "unavailable", "the management catalog has an old or unsupported format; discard the affected data");
            }

            await Assert.That(await ReadSystemDocumentsAsync(directory)).IsEquivalentTo(before);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_catalog_with_an_unsupported_version_is_refused_and_left_unchanged()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-future-").FullName;
        try
        {
            var organization = EntityIds.New().ToString();
            await WriteSystemDocumentsAsync(
                dataDirectory,
                ("catalog", "version", JsonSerializer.Serialize("99")),
                ("organizations", organization, $$"""{"id":"{{organization}}","name":"Acme","createdBy":"alice","createdAt":"2026-10-04T12:00:00+00:00"}"""),
                ($"organizations/{organization}/members", "alice", JsonSerializer.Serialize("alice")),
                ("users/alice/organizations", organization, JsonSerializer.Serialize(organization)),
                ("users/alice/organization_names", "acme", JsonSerializer.Serialize(organization)));
            var before = await ReadSystemDocumentsAsync(dataDirectory);

            HttpStatusCode list, create;
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                settings["data-directory"] = dataDirectory;
            }))
            {
                using var mallory = api.As(EntityIds.New().ToString());
                list = await StatusAsync(mallory.GetAsync(Uri("/v1/management/organizations")));
                create = await StatusAsync(mallory.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "Other" }));
                const string message = "the management catalog has an old or unsupported format; discard the affected data";
                await ErrorAssert.ErrorAsync(mallory.GetAsync(Uri("/v1/management/organizations")), HttpStatusCode.ServiceUnavailable, "unavailable", message);
                await ErrorAssert.ErrorAsync(mallory.GetAsync(Uri($"/v1/management/organizations/{organization}/members")),
                    HttpStatusCode.ServiceUnavailable, "unavailable", message);
                await ErrorAssert.ErrorAsync(mallory.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "Other" }),
                    HttpStatusCode.ServiceUnavailable, "unavailable", message);
                var projectPath = $"/v1/management/organizations/{organization}/projects/{EntityIds.New()}";
                var databasePath = $"/databases/{EntityIds.New()}";
                foreach (var suffix in new[] { databasePath + "/backups", databasePath + "/collections", databasePath + "/indexes", "/saved-queries", databasePath + "/usage" })
                {
                    await ErrorAssert.ErrorAsync(mallory.GetAsync(Uri(projectPath + suffix)), HttpStatusCode.ServiceUnavailable, "unavailable", message);
                }

                await ErrorAssert.ErrorAsync(api.Server.Client.PostAsJsonAsync(Uri("/admin/api-keys"),
                    new { name = "key", scopes = ReadScopes, organizationId = organization }),
                    HttpStatusCode.ServiceUnavailable, "unavailable", message);
                using var commit = await mallory.PostAsJsonAsync(Uri("/v1/management/commits"),
                    new { id = organization, ops = new[] { new { op = "create_organization", id = organization, name = "Other" } } });
                await ErrorAssert.ErrorAsync(commit, HttpStatusCode.ServiceUnavailable, "unavailable", message);
            }

            await Assert.That(list).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(create).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            // Ownership, memberships, name claims and the version marker are exactly as written.
            await Assert.That(await ReadSystemDocumentsAsync(dataDirectory)).IsEquivalentTo(before);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_live_catalog_version_change_refuses_reads_and_writes_until_the_supported_version_is_restored()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Preserved");
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        byte[] supportedVersion;
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            supportedVersion = (await transaction.GetAsync("catalog", "version", CancellationToken.None))!.Json.ToArray();
            await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes("\"99\""), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var before = await ReadSystemDocumentsAsync(api.Server.DataDirectory);
        const string message = "the management catalog has an old or unsupported format; discard the affected data";
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri("/v1/management/organizations")),
            HttpStatusCode.ServiceUnavailable, "unavailable", message);
        await ErrorAssert.ErrorAsync(api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{organization}"), new { name = "Refused" }),
            HttpStatusCode.ServiceUnavailable, "unavailable", message);
        // This endpoint reaches the SystemCatalog write directly, without a separate management-service check.
        await ErrorAssert.ErrorAsync(api.Server.Client.PostAsJsonAsync(Uri("/admin/api-keys"),
            new { name = "Refused key", scopes = ReadScopes, organizationId = organization }),
            HttpStatusCode.ServiceUnavailable, "unavailable", message);
        await Assert.That(await ReadSystemDocumentsAsync(api.Server.DataDirectory)).IsEquivalentTo(before);

        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync("catalog", "version", supportedVersion, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        using var read = await api.Client.GetAsync(Uri($"/v1/management/organizations/{organization}"));
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()).IsEqualTo("Preserved");
        using var renamed = await api.Client.PatchAsJsonAsync(Uri($"/v1/management/organizations/{organization}"), new { name = "Recovered" });
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()).IsEqualTo("Recovered");
        using var issued = await api.Server.Client.PostAsJsonAsync(Uri("/admin/api-keys"),
            new { name = "Recovered key", scopes = ReadScopes, organizationId = organization });
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    [Arguments("organization-tombstone")]
    [Arguments("project-binding")]
    [Arguments("organization-deletion-history")]
    [Arguments("dashboard_query_shapes")]
    [Arguments("dashboard_query_shapes_flush")]
    public async Task Unversioned_management_residuals_are_refused_without_stamping_the_catalog(string residual)
    {
        await using var api = await ManagementApi.StartAsync();
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var organization = EntityIds.New().ToString();
        var id = EntityIds.New().ToString();
        var collection = residual switch
        {
            "organization-tombstone" or "organization-deletion-history" => "organizations",
            "project-binding" => "project_organizations",
            _ => residual,
        };
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync(collection, id, JsonSerializer.SerializeToUtf8Bytes(organization), CancellationToken.None);
            if (residual == "organization-tombstone")
            {
                await Assert.That(await transaction.TombstoneAsync(collection, id, CancellationToken.None)).IsNotNull();
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        // A deletion leaves its tombstone, which counts as catalog state.
        if (residual == "organization-deletion-history")
        {
            await using var deleting = await system.BeginTransactionAsync(write: true, CancellationToken.None);
            await deleting.DeleteAsync(collection, id, CancellationToken.None);
            await deleting.CommitAsync(CancellationToken.None);
        }

        DocumentVersion? version;
        long offset;
        await using (var transaction = await system.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await transaction.GetAsync("catalog", "version", CancellationToken.None)).IsNull();
            await Assert.That((await transaction.ListAsync("organizations", null, 1, 1024, CancellationToken.None)).Documents).IsEmpty();
            version = await transaction.GetVersionAsync(collection, id, CancellationToken.None);
            offset = await transaction.GetOffsetAsync(CancellationToken.None);
        }

        var before = await ReadSystemDocumentsAsync(api.Server.DataDirectory);
        const string message = "the management catalog has an old or unsupported format; discard the affected data";
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri("/v1/management/organizations")),
            HttpStatusCode.ServiceUnavailable, "unavailable", message);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = "Refused" }),
            HttpStatusCode.ServiceUnavailable, "unavailable", message);
        await Assert.That(await ReadSystemDocumentsAsync(api.Server.DataDirectory)).IsEquivalentTo(before);
        await using var read = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.GetAsync("catalog", "version", CancellationToken.None)).IsNull();
        await Assert.That(await read.GetVersionAsync(collection, id, CancellationToken.None)).IsEqualTo(version);
        await Assert.That(await read.GetOffsetAsync(CancellationToken.None)).IsEqualTo(offset);
    }

    [Test]
    public async Task Operator_only_system_documents_allow_fresh_catalog_initialization()
    {
        await using var api = await ManagementApi.StartAsync();
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var id = EntityIds.New().ToString();
        var key = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            name = "Operator key",
            scopes = ReadScopes,
            organizationId = EntityIds.New().ToString(),
        });
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(await transaction.GetAsync("catalog", "version", CancellationToken.None)).IsNull();
            await transaction.PutAsync("api_keys", id, key, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        using var listed = await api.Client.GetAsync(Uri("/v1/management/organizations"));
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var organization = await api.CreateOrganizationAsync("Fresh");
        using var read = await api.Client.GetAsync(Uri($"/v1/management/organizations/{organization}"));
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await using var transactionAfterInitialization = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await transactionAfterInitialization.GetAsync("catalog", "version", CancellationToken.None)).IsNotNull();
        var preserved = await transactionAfterInitialization.GetAsync("api_keys", id, CancellationToken.None);
        await Assert.That(preserved).IsNotNull();
        // The catalog stores canonical JSON (fields in name order), like every lattice database.
        using var stored = JsonDocument.Parse(preserved!.Json);
        using var written = JsonDocument.Parse(key);
        await Assert.That(JsonElement.DeepEquals(stored.RootElement, written.RootElement)).IsTrue();
    }

    // Seeds current-format system documents after real-server bootstrap.
    // In a data directory a server of this build already started on: an unprepared directory with files is refused.
    private static async Task WriteSystemDocumentsAsync(string dataDirectory, params (string Collection, string Id, string Body)[] documents)
    {
        await TerraBaseTestServer.InitializeDataDirectoryAsync(dataDirectory);
        await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "_system.db"), documents);
    }

    // Every document of the _system database, as "collection|id|body" lines in key order.
    private static Task<List<string>> ReadSystemDocumentsAsync(string dataDirectory) =>
        DocumentFiles.ReadAsync(Path.Combine(dataDirectory, "_system.db"));

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
            (Level.Organization, () => client.PutAsJsonAsync(Uri($"{organization}/plan"), new { plan = "pro" })),
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
            using var response = await send();
            statuses.Add(response.StatusCode);
            await ErrorAssert.ErrorAsync(response, HttpStatusCode.NotFound, "not_found", "the resource does not exist");
        }

        return statuses;
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private enum Level
    {
        Organization,
        Project,
        Database,
    }
}
