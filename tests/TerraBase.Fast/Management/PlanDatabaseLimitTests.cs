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
    private const string AllScopes = "databases:read databases:write documents:read documents:write";
    private const string HobbyLimit =
        "the organization's hobby plan allows 1 database; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing";

    // Every route that makes a new database counts the organization's live databases across all of its projects.
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
