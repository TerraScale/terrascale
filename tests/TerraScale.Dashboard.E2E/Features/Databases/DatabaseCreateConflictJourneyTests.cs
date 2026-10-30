using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseCreateConflictJourneyTests(DashboardRootFixture fixture)
    {
        // Only TerraBase's name_taken means the name is taken; any other 409 keeps its own code instead of claiming the name exists.
        [Test]
        public async Task A_create_conflict_other_than_name_taken_keeps_the_terrabase_code()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            _ = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");

            using (var control = await producer.PostAsJsonAsync("/__test/control", new { command = "create-conflict", value = "database_limit" }))
            {
                await Assert.That(control.IsSuccessStatusCode).IsTrue();
            }

            using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "fresh", ["allRegions"] = "true" }))
            {
                var text = await refused.Content.ReadAsStringAsync();
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(text);
                using var problem = JsonDocument.Parse(text);
                await Assert.That(problem.RootElement.GetProperty("code").GetString()).IsEqualTo("database_limit");
                await Assert.That(text).DoesNotContain("already exists");
            }

            using (var control = await producer.PostAsJsonAsync("/__test/control", new { command = "create-conflict", value = "" }))
            {
                await Assert.That(control.IsSuccessStatusCode).IsTrue();
            }

            using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "fresh", ["allRegions"] = "true" }))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            }

            using (var duplicate = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "fresh", ["allRegions"] = "true" }))
            {
                await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                await Assert.That(await duplicate.Content.ReadAsStringAsync()).Contains("already exists");
            }
        }
    }
}
