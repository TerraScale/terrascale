using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class PlanDatabaseLimitTests
{
    private static readonly Action<IServiceCollection> PlanAllowances = services => services.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance()));
    private const string AllScopes = "databases:read databases:write documents:read documents:write";
    private const string HobbyLimit =
        "the organization's hobby plan allows 1 database; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing";

    // Every route that makes a new database counts the organization's live databases across all of its projects.
    [Test]
    public async Task A_hobby_organization_is_refused_a_second_database_on_every_create_route()
    {
        await using var api = await ManagementApi.StartAsync(services: PlanAllowances);
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Shop");
        var other = await api.CreateProjectAsync(organization, "Blog");
        var main = await api.CreateDatabaseAsync(organization, project, "main");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        using var data = api.DataClient(await api.Server.IssueAsync(organization, "documents:read", "documents:write"));

        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(databases), new { name = "second" }), HttpStatusCode.Conflict, "database_limit", HobbyLimit);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects/{other}/databases"), new { name = "second" }),
            HttpStatusCode.Conflict, "database_limit", HobbyLimit);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"{databases}/{main}/branches"), new { name = "preview" }), HttpStatusCode.Conflict, "database_limit", HobbyLimit);
        await ErrorAssert.ErrorAsync(data.PutAsync(Uri($"/v1/projects/{project}/databases/second"), null), HttpStatusCode.Conflict, "database_limit", HobbyLimit);

        // Replaying an existing create still finds it.
        using var replay = await data.PutAsync(Uri($"/v1/projects/{project}/databases/main"), null);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(databases)));
        await Assert.That(list.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task A_starter_organization_holds_five_live_databases_and_frees_a_slot_on_delete()
    {
        await using var api = await ManagementApi.StartAsync(services: PlanAllowances);
        var organization = await api.CreateOrganizationAsync("Acme");
        var project = await api.CreateProjectAsync(organization, "Shop");
        await api.SetPlanAsync(api.User, organization, "starter");
        var ids = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            ids.Add(await api.CreateDatabaseAsync(organization, project, "db" + i));
        }

        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(databases), new { name = "db6" }), HttpStatusCode.Conflict, "database_limit",
            "the organization's starter plan allows 5 databases; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing");
        using (var deleted = await api.Client.DeleteAsync(Uri($"{databases}/{ids[0]}")))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        using var created = await api.Client.PostAsJsonAsync(Uri(databases), new { name = "db6" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    // The dashboard publishes a paid plan before the organization's first database; enrollment adopts the memberless record.
    [Test]
    public async Task The_operator_publishes_an_organization_plan_before_its_first_database_and_enrollment_keeps_it()
    {
        await using var api = await ManagementApi.StartAsync(services: PlanAllowances);
        var organization = EntityIds.New().ToString();
        var plan = Uri($"/admin/organizations/{organization}/plan");

        await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(Uri("/admin/organizations/not-a-ulid/plan"), new { plan = "starter" }),
            HttpStatusCode.BadRequest, "invalid_request", "organization must be a canonical uppercase ULID");
        await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(plan, new { plan = "hobby" }), HttpStatusCode.NotFound, "not_found", "the resource does not exist");
        using (var set = await api.Server.Client.PutAsJsonAsync(plan, new { plan = "starter" }))
        {
            await Assert.That(set.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await set.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("id").GetString()).IsEqualTo(organization);
            await Assert.That(body.GetProperty("plan").GetString()).IsEqualTo("starter");
        }

        // Republishing the same plan writes nothing, so it records no audit event.
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var before = (await engine.OpenSystemDatabaseAsync(CancellationToken.None)).CommitSequence;
        using (var again = await api.Server.Client.PutAsJsonAsync(plan, new { plan = "starter" }))
        {
            await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That((await engine.OpenSystemDatabaseAsync(CancellationToken.None)).CommitSequence).IsEqualTo(before);

        // A Hobby organization could not hold the second database.
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = AllScopes;
            token.Claims["org_id"] = organization;
        }));
        foreach (var name in new[] { "main", "second" })
        {
            using var created = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        var enrolled = await api.Server.Services.GetRequiredService<IProjectCatalog>().ProjectOrganizationAsync(project, CancellationToken.None);
        await Assert.That(enrolled is string id && id == organization).IsTrue();
        using (var hobby = await api.Server.Client.PutAsJsonAsync(plan, new { plan = "hobby" }))
        {
            await Assert.That(hobby.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "third" }), HttpStatusCode.Conflict, "database_limit", HobbyLimit);
    }

    [Test]
    public async Task The_organization_plan_route_leaves_owner_managed_organizations_to_their_owners()
    {
        await using var api = await ManagementApi.StartAsync(services: PlanAllowances);
        var organization = await api.CreateOrganizationAsync("Acme");

        await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(Uri($"/admin/organizations/{organization}/plan"), new { plan = "enterprise" }),
            HttpStatusCode.NotFound, "not_found", "the resource does not exist");
        using var onClient = await api.Client.PutAsJsonAsync(Uri($"/admin/organizations/{EntityIds.New()}/plan"), new { plan = "enterprise" });
        await Assert.That(onClient.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
