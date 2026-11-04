using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Usage
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class UsagePlacementJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task PageAndMcpCountTheUnionOfHomeRegions()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, builder =>
            {
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageMeasurements>(new ReferenceUsageMeasurements()));
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageReplication>(new ReferenceUsageReplication()));
            });
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
            var projectId = identity.RootElement.GetProperty("projectId").GetString()!;
            using (var catalog = await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "fra", "gru", "sfo" } }))
            {
                await Assert.That(catalog.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            using (var admission = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId }))
            {
                await Assert.That(admission.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            var secret = await HttpJourney.CreatePatAsync(client, "Usage placement", account.Password);
            await AssertSummaryAsync(0, "0");
            await CallAsync("create_database", new() { ["name"] = "usage-gru", ["region"] = "GRU" });
            await AssertSummaryAsync(1, "1");
            await CallAsync("create_database", new() { ["name"] = "usage-sfo", ["region"] = "SFO" });
            await AssertSummaryAsync(2, "2");
            await CallAsync("create_database", new() { ["name"] = "usage-overlap", ["region"] = "GRU" });
            await AssertSummaryAsync(2, "2");

            async Task<JsonElement> CallAsync(string name, Dictionary<string, object?> arguments)
            {
                arguments["organizationId"] = organizationId;
                arguments["projectId"] = projectId;
                var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
                var result = response.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.ToString());
                return result.GetProperty("structuredContent");
            }
            async Task AssertSummaryAsync(int? expected, string label)
            {
                var report = await CallAsync("get_usage", new());
                var count = report.GetProperty("regionCount");
                if (expected is { } value)
                {
                    await Assert.That(count.GetInt32()).IsEqualTo(value);
                }
                else
                {
                    await Assert.That(count.ValueKind).IsEqualTo(JsonValueKind.Null);
                }

                await Assert.That(report.GetProperty("regionCountDescription").GetString()).IsEqualTo(label);
                var html = WebUtility.HtmlDecode(await client.GetStringAsync("/usage?project=" + projectId));
                await Assert.That(html).Contains("Regions in project</dt><dd class=\"ts-stat-value\">" + label + "</dd>");
            }
        }

        [Test]
        public async Task PlacementNormalizesOverlapsWithoutUsingProjectDefaultsOrForeignDatabases()
        {
            var project = new ProjectRecord { Id = "01JA0000000000000000000001", Region = "UNUSED", Regions = ["UNUSED"] };
            var database = new DatabaseRecord { ProjectId = project.Id, Region = " GRU ", Regions = ["gru", "SJC", "fra", "FRA", ""] };
            project.Databases.Add("01JA0000000000000000000002", database);
            await Assert.That(UsagePlacement.For(project).RegionCount).IsEqualTo(3);
            database.Regions.Clear();
            database.Region = "";
            await Assert.That(UsagePlacement.For(project).RegionCount).IsNull();
            database.ProjectId = "01JA0000000000000000000003";
            await Assert.That(() => UsagePlacement.For(project)).Throws<IOException>();
        }
    }
}
