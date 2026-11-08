using System.Net;
using System.Net.Http.Headers;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseRenameJourneyTests(DashboardRootFixture fixture)
    {
        // Rename validates the name exactly like create: an underscore is refused with create's message.
        [Test]
        public async Task RenameRefusesNamesCreateRefuses()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            var id = await HttpJourney.CreateDatabaseAsync(client, "rename-rules");
            var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/settings");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            using var refused = await client.PostAsync("/api/v1/dashboard/databases/rename", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "has_underscore", ["confirmName"] = "rename-rules" }));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var page = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
            await Assert.That(page).Contains(DatabaseService.NameHint);
            await Assert.That(page).Contains("value=\"has_underscore\"");
        }
    }
}
