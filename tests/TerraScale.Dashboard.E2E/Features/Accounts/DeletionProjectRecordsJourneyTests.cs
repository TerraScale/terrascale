using System.Net;

using Scenario = TerraScale.Dashboard.E2E.DeletionProjectRecordsScenario;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DeletionProjectRecordsJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task CleanupReplaysWithoutRemovingOtherProjectsOrOrganizations()
        {
            await using var scenario = await Scenario.StartAsync(fixture.Root);
            await CleanAsync(scenario, HttpStatusCode.NoContent);
            await scenario.SeedAsync();
            var retained = new Dictionary<string, string>();
            foreach (var collection in Scenario.Records)
            {
                retained[collection] = (await scenario.GetAsync(collection, scenario.Retained))!.Document.GetRawText();
            }

            await CleanAsync(scenario, HttpStatusCode.NoContent);
            await CleanAsync(scenario, HttpStatusCode.NoContent);
            foreach (var collection in Scenario.Records)
            {
                await Assert.That(await scenario.GetAsync(collection, scenario.Project)).IsNull();
                await Assert.That((await scenario.GetAsync(collection, scenario.Retained))!.Document.GetRawText()).IsEqualTo(retained[collection]);
            }
            foreach (var collection in Scenario.Indexes)
            {
                var projects = await scenario.IndexAsync(collection);
                await Assert.That(projects.Count).IsEqualTo(2);
                await Assert.That(projects[scenario.Retained]).IsEqualTo(scenario.Organization);
                await Assert.That(projects[scenario.ForeignProject]).IsEqualTo(scenario.ForeignOrganization);
            }
        }

        [Test]
        [Arguments("dashboard_sql_workspaces", false)]
        [Arguments("dashboard_sql_workspaces", true)]
        [Arguments("dashboard_saved_queries", false)]
        [Arguments("dashboard_alert_rules", false)]
        [Arguments("dashboard_log_drains", false)]
        [Arguments("dashboard_alert_projects", false)]
        [Arguments("dashboard_log_drain_projects", false)]
        [Arguments("dashboard_saved_queries", true)]
        [Arguments("dashboard_alert_rules", true)]
        [Arguments("dashboard_log_drains", true)]
        [Arguments("dashboard_alert_projects", true)]
        [Arguments("dashboard_log_drain_projects", true)]
        public async Task MismatchedOwnershipOrDocumentIdentityIsRetryableAndRetained(string collection, bool wrongDocumentId)
        {
            await using var scenario = await Scenario.StartAsync(fixture.Root);
            await scenario.SeedAsync();
            var project = Scenario.Indexes.Contains(collection) ? "all" : scenario.Project;
            await scenario.SeedMismatchAsync(collection, wrongDocumentId);
            var unchanged = (await scenario.GetAsync(collection, project))!;
            await CleanAsync(scenario, HttpStatusCode.ServiceUnavailable);
            await Assert.That((await scenario.GetAsync(collection, project))!.Version).IsEqualTo(unchanged.Version);
            foreach (var records in Scenario.Records)
            {
                await Assert.That(await scenario.GetAsync(records, scenario.Retained)).IsNotNull();
            }
        }

        [Test]
        public async Task AccountErasureRemovesPrivateWorkspacesAcrossProjectsAndReplays()
        {
            await using var scenario = await Scenario.StartAsync(fixture.Root);
            await scenario.SeedAsync();
            for (var replay = 0; replay < 2; replay++)
            {
                using var response = await scenario.Client.PostAsync("/__test/sql-user/" + scenario.User, null);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                foreach (var project in new[] { scenario.Project, scenario.Retained })
                {
                    await Assert.That(await scenario.GetAsync("dashboard_sql_workspaces", project)).IsNull();
                    await Assert.That(await scenario.GetAsync("dashboard_saved_queries", project)).IsNotNull();
                }
            }
        }

        [Test]
        public async Task ConcurrentIndexUpdateSurvivesFailedCleanupAndJournalReplay()
        {
            await using var scenario = await Scenario.StartAsync(fixture.Root);
            await scenario.SeedAsync();
            var concurrentProject = scenario.ArrangeConcurrentIndexUpdate();
            await CleanAsync(scenario, HttpStatusCode.ServiceUnavailable);
            await CleanAsync(scenario, HttpStatusCode.NoContent);
            var result = await scenario.IndexAsync("dashboard_alert_projects");
            await Assert.That(result.ContainsKey(scenario.Project)).IsFalse();
            await Assert.That(result[concurrentProject]).IsEqualTo(scenario.ForeignOrganization);
            await Assert.That(result[scenario.Retained]).IsEqualTo(scenario.Organization);
        }

        private static async Task CleanAsync(Scenario scenario, HttpStatusCode expected)
        {
            using var response = await scenario.Client.PostAsync($"/__test/project-records/{scenario.Organization}/{scenario.Project}", null);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
        }
    }
}
