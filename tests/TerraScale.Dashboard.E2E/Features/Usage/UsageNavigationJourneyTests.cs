using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Usage
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class UsageNavigationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments(false)]
        [Arguments(true)]
        public async Task UsageTabsRetainPeriodAndDatabaseWhileProjectLinksClearThePreviousDatabase(bool selectDatabase)
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, builder =>
            {
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageMeasurements>(new ReferenceUsageMeasurements()));
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageReplication>(new ReferenceUsageReplication()));
            });
            using var client = scenario.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var projectId = identity.RootElement.GetProperty("projectId").GetString()!;
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "usage-navigation", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var createdDatabaseId = database.RootElement.GetProperty("id").GetString()!;
            var databaseId = selectDatabase ? createdDatabaseId : "";
            // Test admission permits a second project; it does not simulate a paid checkout.
            using var producer = scenario.CreateProviderClient();
            using (var admission = await producer.PostAsJsonAsync("/__test/control", new
            { command = "plan", organizationId = identity.RootElement.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId }))
            {
                await Assert.That(admission.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            using var project = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects", new()
            { ["_csrf"] = csrf, ["name"] = "Usage navigation second", ["region"] = "GRU" });
            await Assert.That(project.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);

            var initial = await PageAsync("/usage?range=30d&database=" + databaseId);
            var rangeLink = Link(initial, "24h");
            var rangeQuery = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, rangeLink).Query);
            await Assert.That(rangeQuery["range"].ToString()).IsEqualTo("24h");
            await Assert.That(rangeQuery["project"].ToString()).IsEqualTo(projectId);
            await Assert.That(rangeQuery["database"].ToString()).IsEqualTo(databaseId);
            var settings = Link(initial, "Spending");
            await Assert.That(QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, settings).Query)["tab"].ToString()).IsEqualTo("settings");
            await AssertSelectionAsync(settings, projectId, databaseId);
            var overview = Link(await PageAsync(settings), "Overview");
            await Assert.That(QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, overview).Query)["tab"].ToString()).IsEqualTo("overview");
            await AssertSelectionAsync(overview, projectId, databaseId);
            var restored = await PageAsync(overview);
            await Assert.That(restored).Contains("value=\"30d\" selected=\"selected\"");
            var databaseFilter = DatabaseFilter(restored);
            if (selectDatabase)
            {
                await Assert.That(databaseFilter).Contains("value=\"" + databaseId + "\" selected=\"selected\"");
            }
            else
            {
                await Assert.That(databaseFilter.Contains("selected=", StringComparison.Ordinal)).IsFalse();
            }

            var other = Link(initial, "Usage navigation second");
            var query = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, other).Query);
            var otherProjectId = query["project"].ToString();
            await Assert.That(otherProjectId.Length).IsGreaterThan(0);
            await Assert.That(otherProjectId).IsNotEqualTo(projectId);
            await AssertSelectionAsync(other, otherProjectId, "");
            var switched = await PageAsync(other);
            await Assert.That(switched).Contains("value=\"30d\" selected=\"selected\"");
            await Assert.That(DatabaseFilter(switched)).Contains("<option value=\"\">All databases</option>");
            await Assert.That(DatabaseFilter(switched).Contains("selected=", StringComparison.Ordinal)).IsFalse();
            await AssertSelectionAsync(Link(switched, "Spending"), otherProjectId, "");

            // Explicit missing/foreign filters must not render a broader successful report.
            foreach (var path in new[] {
                "/usage?project=01JA0000000000000000000999",
                "/usage?project=" + projectId + "&database=01JA0000000000000000000999",
                "/usage?project=" + otherProjectId + "&database=" + createdDatabaseId,
                "/usage?tab=settings&project=01JA0000000000000000000999" })
            {
                using var rejected = await client.GetAsync(path);
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("is unavailable.");
            }
            var defaults = await PageAsync("/usage");
            await Assert.That(defaults).Contains("value=\"7d\" selected=\"selected\"");
            await Assert.That(DatabaseFilter(defaults).Contains("selected=", StringComparison.Ordinal)).IsFalse();
            await Assert.That(initial).Contains("data-on:change=\"el.form.elements.namedItem('database').value = ''; el.form.requestSubmit()\"");

            async Task<string> PageAsync(string path)
            {
                using var response = await client.GetAsync(path);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            }
            async Task AssertSelectionAsync(string href, string expectedProject, string expectedDatabase)
            {
                var values = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, href).Query);
                await Assert.That(values["project"].ToString()).IsEqualTo(expectedProject);
                await Assert.That(values["range"].ToString()).IsEqualTo("30d");
                await Assert.That(values.GetValueOrDefault("database").ToString()).IsEqualTo(expectedDatabase);
                if (expectedDatabase.Length == 0)
                {
                    await Assert.That(values.ContainsKey("database")).IsFalse();
                }
            }
        }

        [Test]
        public async Task SelectionRejectsWrongOrganizationProjectsAndPreservesAnAbsentProject()
        {
            var organization = new OrganizationRecord { Id = "01JA0000000000000000000001" };
            organization.Members.Add("user", new() { UserId = "user", Status = "active", Role = "owner" });
            var foreign = new ProjectRecord { Id = "01JA0000000000000000000002", OrganizationId = "01JA0000000000000000000003" };
            organization.Projects.Add(foreign.Id, foreign);
            var context = new AccountContext(new("user", "Test", "synthetic@example.test", "test-only", null, DateTimeOffset.UtcNow.AddMinutes(5)),
                new() { UserId = "user" }, [organization], organization, null);
            var selection = UsageSelection.For(context, "", "", "");
            await Assert.That(selection.Context.Project).IsNull();
            await Assert.That(selection.DatabaseId).IsEqualTo("");
            await Assert.That(selection.Range).IsEqualTo("7d");
            await Assert.That(() => UsageSelection.For(context, foreign.Id, "", "")).Throws<AccountAccessException>();
            await Assert.That(() => UsageSelection.For(context, "", "01JA0000000000000000000004", "")).Throws<AccountAccessException>();
        }

        private static string DatabaseFilter(string html)
        {
            return Regex.Match(html, "<select[^>]*id=\"usage-database\"[^>]*>.*?</select>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Value;
        }

        private static string Link(string html, string label)
        {
            var match = Regex.Match(html, "<a[^>]*href=\"([^\"]+)\"[^>]*>(?:<svg\\b[^>]*>(?:(?!</svg>).)*</svg>)?(?:<span>)?" + Regex.Escape(label) + "(?:</span>)?</a>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("Missing usage link: " + label);
        }
    }
}
