using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardRootFixture>]
public sealed class ProjectLimitJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task FreePlanRefusesASecondProjectAndFlagsAnOrganizationAlreadyOverItsLimit()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
        await Assert.That(await ProjectCountAsync(client, organizationId)).IsEqualTo(1);

        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=projects");
        using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects", new()
        { ["_csrf"] = csrf, ["name"] = "Second project", ["regions"] = "GRU", ["region"] = "GRU" }))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            var html = await refused.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("project limit (1 project)");
            await Assert.That(html.Split("project limit (1 project)").Length - 1).IsEqualTo(1)
                .Because("The open create-project dialog states the refusal once, not again behind it.");
            await Assert.That(html).Contains("Billing tab");
        }
        await Assert.That(await ProjectCountAsync(client, organizationId)).IsEqualTo(1).Because("A refused project is never created.");

        // Test admission permits a second project; returning to Free leaves an organization over its limit.
        await PlanAsync(producer, organizationId, OrganizationRecord.StarterPlanId);
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects", new()
        { ["_csrf"] = csrf, ["name"] = "Second project", ["regions"] = "GRU", ["region"] = "GRU" }))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That((await client.GetStringAsync("/organization")).Contains("project-limit-exceeded", StringComparison.Ordinal)).IsFalse();
        await PlanAsync(producer, organizationId, OrganizationRecord.FreePlanId);
        var flagged = await client.GetStringAsync("/organization");
        await Assert.That(flagged).Contains("This organization has 2 projects; its plan allows 1.");
        await Assert.That(await ProjectCountAsync(client, organizationId)).IsEqualTo(2).Because("Flagging never deletes a project.");
    }

    private static async Task PlanAsync(HttpClient producer, string organizationId, string planId)
    {
        using var response = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = planId });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static async Task<int> ProjectCountAsync(HttpClient client, string organizationId) =>
        (await Context(client)).GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == organizationId)
            .GetProperty("projects").EnumerateObject().Count();

    private static async Task<JsonElement> Context(HttpClient client) => JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/context")).RootElement.Clone();
}
