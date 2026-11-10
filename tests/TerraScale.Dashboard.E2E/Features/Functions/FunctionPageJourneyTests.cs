using System.Net;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Functions
{
    [ClassDataSource<SharedDatabaseSdk>(Shared = SharedType.PerTestSession)]
    public sealed class FunctionPageJourneyTests(SharedDatabaseSdk shared)
    {
        [Test]
        public async Task OwnerOpensFunctionsTab()
        {
            var scenario = shared.Scenario;
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
