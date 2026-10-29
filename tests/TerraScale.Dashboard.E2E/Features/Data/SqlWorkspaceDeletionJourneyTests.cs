using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class SqlWorkspaceDeletionJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task DatabaseDeletionMarkersStayBoundedWithoutGrowingOrganizationMetadata()
        {
            var state = new SqlWorkspaceState();
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, state.Configure, configureApp: state.MapRoutes);
            using var client = scenario.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var response = await client.PostAsync("/__test/sql-marker-capacity", null);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(result.GetProperty("count").GetInt32()).IsEqualTo(4100);
            foreach (var property in new[] { "unchanged", "bounded", "scoped" })
            {
                await Assert.That(result.GetProperty(property).GetBoolean()).IsTrue();
            }
        }

    }
}
