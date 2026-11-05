using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class StateSqlJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task RegionalStateUsesGruAndRefusesAMissingHomePeer()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root, regional: true);
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            using var client = scenario.CreateClient();
            using (var ready = await client.GetAsync("/readyz"))
            {
                await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            await scenario.RemoveStateHomePeerAsync();
            using var unavailable = await client.GetAsync("/readyz");
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await unavailable.Content.ReadAsStringAsync()).Contains("\"terrabase\":\"unavailable\"");
        }

        [Test]
        public async Task SignUpStateSurvivesAColdRestartOfBothReplicas()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            using var controls = scenario.CreateProviderClient();
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var email = "state-sql-" + suffix + "@example.test";
            var password = "State-SQL-" + suffix;
            using (var signUp = await HttpJourney.LoginAsync(client, email, password, "/onboarding", signUp: true))
            {
                await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            using (var login = await HttpJourney.LoginAsync(replica, email, password))
            {
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var before = await ObserveAsync(controls);
            await Assert.That(Rows(before, "dashboard_accounts")).IsEqualTo(1);
            await Assert.That(Rows(before, "sessions")).IsEqualTo(2);
            await Assert.That(await scenario.RestartAsync()).IsTrue();
            using var cold = scenario.CreateReplacementClient(client);
            using var me = await cold.GetAsync("/api/v1/auth/me");
            await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            await Assert.That(json.RootElement.GetProperty("email").GetString()).IsEqualTo(email);
            var after = await ObserveAsync(controls);
            await Assert.That(after.GetProperty("rowDigest").GetString()).IsEqualTo(before.GetProperty("rowDigest").GetString());
            await Assert.That(after.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        [Test]
        public async Task AMissingTableFailsReadinessAndAdmitsNoRequest()
        {
            await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
            using var controls = scenario.CreateProviderClient();
            await Assert.That(await scenario.StartHostsAsync()).IsTrue();
            using var client = scenario.CreateClient();
            using (var ready = await client.GetAsync("/readyz"))
            {
                await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            using (var drop = await controls.PostAsJsonAsync("/__test/control", new { command = "drop-table", name = "dashboard_accounts" }))
            {
                drop.EnsureSuccessStatusCode();
            }

            using var missing = await client.GetAsync("/readyz");
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await missing.Content.ReadAsStringAsync()).Contains("\"terrabase\":\"unavailable\"");
        }

        private static async Task<JsonElement> ObserveAsync(HttpClient controls)
        {
            return await controls.GetFromJsonAsync<JsonElement>("/__test/observations");
        }

        private static int Rows(JsonElement observation, string table)
        {
            return observation.GetProperty("tables").EnumerateArray().Single(row => row.GetProperty("name").GetString() == table).GetProperty("rows").GetInt32();
        }
    }
}
