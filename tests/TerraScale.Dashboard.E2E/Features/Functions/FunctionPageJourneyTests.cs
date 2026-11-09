using System.Net;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Functions
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class FunctionPageJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task OwnerOpensFunctionsTab()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            var id = await HttpJourney.CreateDatabaseAsync(client, "functions-journey");
            using var page = await client.GetAsync("/databases/" + id + "/functions");
            var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(html).DoesNotContain("Functions need a role");
        }
    }
}
