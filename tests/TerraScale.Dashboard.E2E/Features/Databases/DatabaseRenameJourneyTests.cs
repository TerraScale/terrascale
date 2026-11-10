using System.Net;
using System.Net.Http.Headers;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<SharedDatabaseSdk>(Shared = SharedType.PerTestSession)]
    public sealed class DatabaseRenameJourneyTests(SharedDatabaseSdk shared)
    {
        [Test]
        public async Task RenameRefusesNamesCreateRefuses()
        {
            var scenario = shared.Scenario;
            using var client = scenario.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            var id = await HttpJourney.CreateDatabaseAsync(client, "rename-rules");
            var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/settings");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            foreach (var name in new[] { "1rename", "Rename", new string('a', 64), "con" })
            {
                using var refused = await client.PostAsync("/api/v1/dashboard/databases/rename", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = name, ["confirmName"] = "rename-rules" }));
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                var page = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
                await Assert.That(page).Contains(DatabaseService.NameHint);
                await Assert.That(page).Contains("value=\"" + name + "\"");
            }

            using var accepted = await client.PostAsync("/api/v1/dashboard/databases/rename", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "has_underscore", ["confirmName"] = "rename-rules" }));
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
    }
}
