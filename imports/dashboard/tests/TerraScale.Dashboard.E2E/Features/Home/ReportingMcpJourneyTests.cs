using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Home;

namespace TerraScale.Dashboard.E2E.Features.Home;

[ClassDataSource<DashboardRootFixture>]
public sealed class ReportingMcpJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ExplicitAuthorizedScopesPersistAcrossReplicasWithoutChangingSelection()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root, builder =>
            builder.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements, TerraBaseHomeMeasurements>()));
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var persona = scenario.Personas.Seeded;
        using var login = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var replicaLogin = await HttpJourney.LoginAsync(replica, persona.Email, persona.Password);
        await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(browser, "TUnit explicit reporting scope", persona.Password);
        using var before = JsonDocument.Parse(await browser.GetStringAsync("/api/v1/dashboard/context"));
        var selectedOrganization = before.RootElement.GetProperty("selection").GetProperty("organizationId").GetString()!;
        var selectedProject = before.RootElement.GetProperty("selection").GetProperty("projectId").GetString()!;
        var personal = before.RootElement.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "personal");
        var organizationId = personal.GetProperty("id").GetString()!;
        var projectId = personal.GetProperty("projects").EnumerateObject().Single().Name;
        await Assert.That(organizationId == selectedOrganization).IsFalse();

        var discovery = await HttpJourney.McpAsync(browser, secret, "tools/list", new { });
        var tools = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
        foreach (var name in new[] { "get_usage", "get_project_settings", "set_audit_retention", "delete_project" })
        {
            await Assert.That(tools.ContainsKey(name)).IsTrue();
            await Assert.That(tools[name].TryGetProperty("outputSchema", out _)).IsTrue();
        }
        await Assert.That(tools.ContainsKey("set_project_authentication")).IsFalse();

        await Assert.That(tools.ContainsKey("set_spending_controls")).IsFalse();
        var usage = await CallAsync("get_usage", new { organizationId, projectId, range = "24h" }, useReplica: true);
        foreach (var name in new[] { "allowances", "allowanceError", "planUsageNote", "projects", "databases", "totals", "activeDatabases", "regionCount", "canManage" })
            await Assert.That(usage.TryGetProperty(name, out _)).IsTrue().Because("Original get_usage report field: " + name);
        foreach (var name in new[] { "spendCapCents", "paidExcessEnabled", "projectEffectiveCapCents" })
            await Assert.That(usage.TryGetProperty(name, out _)).IsFalse();
        var project = usage.GetProperty("projects").EnumerateArray().Single(item => item.GetProperty("id").GetString() == projectId);
        foreach (var name in new[] { "spendCapCents", "effectiveCapCents", "operationLimits" })
            await Assert.That(project.TryGetProperty(name, out _)).IsFalse();
        await Assert.That(usage.GetProperty("totals").GetProperty("range").GetString()).IsEqualTo("24h");

        var selectedUsage = await CallAsync("get_usage", new { organizationId = selectedOrganization, projectId = selectedProject, range = "24h" }, useReplica: true);
        var databaseCount = selectedUsage.GetProperty("activeDatabases").GetInt32();
        await Assert.That(databaseCount > 0).IsTrue();
        using var source = await ReferenceFixtures.ReadManifestAsync(fixture.Root);
        var originalOverview = source.RootElement.GetProperty("mcp").GetProperty("toolsList").EnumerateArray()
            .SelectMany(page => page.GetProperty("result").GetProperty("tools").EnumerateArray()).Single(tool => tool.GetProperty("name").GetString() == "get_overview");
        await Assert.That(tools["get_overview"].GetProperty("outputSchema").GetProperty("properties").EnumerateObject().Select(field => field.Name).ToArray())
            .IsEquivalentTo(originalOverview.GetProperty("outputSchema").GetProperty("properties").EnumerateObject().Select(field => field.Name).ToArray());
        var overview = await CallAsync("get_overview", new { organizationId = selectedOrganization, projectId = selectedProject, range = "24h" }, useReplica: true);
        await Assert.That(overview.GetProperty("databaseCount").GetInt32()).IsEqualTo(databaseCount);
        await Assert.That(overview.GetProperty("organizationMembers").GetInt32()).IsEqualTo(3);
        await Assert.That(overview.GetProperty("activity").GetProperty("sample").GetBoolean()).IsFalse();
        await Assert.That(overview.GetProperty("activity").GetProperty("metered").GetBoolean()).IsTrue();
        await Assert.That(overview.GetProperty("activity").GetProperty("totalReads").GetInt64()).IsEqualTo(12L * databaseCount);
        await Assert.That(overview.GetProperty("activity").GetProperty("totalWrites").GetInt64()).IsEqualTo(7L * databaseCount);
        await Assert.That(overview.GetProperty("activity").GetProperty("labels")[0].GetString()!).Contains(" UTC");
        await Assert.That(overview.GetProperty("gettingStarted")[0].GetProperty("title").GetString()).IsEqualTo("Create first database");
        var totals = selectedUsage.GetProperty("totals");
        await Assert.That(totals.GetProperty("available").GetBoolean()).IsTrue();
        await Assert.That(totals.GetProperty("storageMeasured").GetBoolean()).IsTrue();
        await Assert.That(totals.GetProperty("reads").GetInt64()).IsEqualTo(12L * databaseCount);
        await Assert.That(totals.GetProperty("writes").GetInt64()).IsEqualTo(7L * databaseCount);
        await Assert.That(totals.GetProperty("logicalBytes").GetInt64()).IsEqualTo(4096L * databaseCount);
        var databaseRow = selectedUsage.GetProperty("databases")[0];
        foreach (var name in new[] { "spendCapCents", "effectiveCapCents", "operationLimits" })
            await Assert.That(databaseRow.TryGetProperty(name, out _)).IsFalse();
        await Assert.That(databaseRow.GetProperty("metered").GetBoolean()).IsTrue();
        await Assert.That(databaseRow.GetProperty("documents").GetInt64()).IsEqualTo(2L);
        await Assert.That(databaseRow.GetProperty("reads").GetInt64()).IsEqualTo(12L);
        var filtered = await CallAsync("get_usage", new { organizationId = selectedOrganization, projectId = selectedProject, databaseId = databaseRow.GetProperty("id").GetString(), range = "1h" }, useReplica: true);
        await Assert.That(filtered.GetProperty("databases").GetArrayLength()).IsEqualTo(1);
        await Assert.That(filtered.GetProperty("totals").GetProperty("reads").GetInt64()).IsEqualTo(12L);

        var scenarioCsrf = await HttpJourney.CsrfAsync(replica, "/profile");
        foreach (var unavailable in new[] { "not-metered", "foreign-project", "no-period", "unauthorized" })
        {
            using var armed = await HttpJourney.PostFormAsync(replica, "/__test/reporting/scenario", new()
            { ["_csrf"] = scenarioCsrf, ["project"] = selectedProject, ["scenario"] = unavailable });
            await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            var missing = await CallAsync("get_usage", new { organizationId = selectedOrganization, projectId = selectedProject }, useReplica: true);
            var missingOverview = await CallAsync("get_overview", new { organizationId = selectedOrganization, projectId = selectedProject }, useReplica: true);
            await Assert.That(missingOverview.GetProperty("activity").GetProperty("metered").GetBoolean()).IsFalse().Because(unavailable);
            await Assert.That(missingOverview.GetProperty("activity").GetProperty("totalReads").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(missingOverview.GetProperty("activity").GetProperty("totalWrites").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(missingOverview.GetProperty("activity").GetProperty("reads").GetArrayLength()).IsEqualTo(0);
            var missingTotals = missing.GetProperty("totals");
            await Assert.That(missingTotals.GetProperty("available").GetBoolean()).IsFalse();
            await Assert.That(missingTotals.GetProperty("storageMeasured").GetBoolean()).IsFalse().Because("Project storage must include files; independent document bytes cannot replace it.");
            await Assert.That(missingTotals.GetProperty("logicalBytes").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(missing.GetProperty("projects").EnumerateArray().Single(row => row.GetProperty("id").GetString() == selectedProject).GetProperty("logicalBytes").ValueKind).IsEqualTo(JsonValueKind.Null);
            foreach (var counter in new[] { "reads", "writes" })
                await Assert.That(missingTotals.GetProperty(counter).ValueKind).IsEqualTo(JsonValueKind.Null).Because(unavailable + " must not become measured zeroes");
            foreach (var allowance in missing.GetProperty("allowances").EnumerateArray())
            {
                if (allowance.GetProperty("name").GetString() == "Storage")
                {
                    await Assert.That(allowance.GetProperty("measured").GetBoolean()).IsTrue();
                    await Assert.That(allowance.GetProperty("used").GetInt64()).IsGreaterThan(0);
                    continue;
                }
                await Assert.That(allowance.GetProperty("measured").GetBoolean()).IsFalse();
                await Assert.That(allowance.GetProperty("used").ValueKind).IsEqualTo(JsonValueKind.Null);
                await Assert.That(allowance.GetProperty("percent").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
            foreach (var row in missing.GetProperty("databases").EnumerateArray())
            {
                await Assert.That(row.GetProperty("unavailable").GetBoolean()).IsFalse();
                await Assert.That(row.GetProperty("metered").GetBoolean()).IsFalse();
                await Assert.That(row.GetProperty("documents").GetInt64()).IsGreaterThanOrEqualTo(0);
                await Assert.That(row.GetProperty("logicalBytes").GetInt64()).IsGreaterThanOrEqualTo(0).Because("Independent database document storage remains known.");
                foreach (var counter in new[] { "reads", "writes" })
                    await Assert.That(row.GetProperty(counter).ValueKind).IsEqualTo(JsonValueKind.Null);
            }
        }
        using var restored = await HttpJourney.PostFormAsync(replica, "/__test/reporting/scenario", new()
        { ["_csrf"] = scenarioCsrf, ["project"] = selectedProject, ["scenario"] = "valid" });
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var retention = await CallAsync("set_audit_retention", new { organizationId, projectId, days = 30 });
        await Assert.That(retention.GetProperty("saved").GetBoolean()).IsTrue();
        var settings = await CallAsync("get_project_settings", new { organizationId, projectId }, useReplica: true);
        await Assert.That(settings.GetProperty("projectId").GetString()).IsEqualTo(projectId);
        await Assert.That(settings.GetProperty("auditRetentionDays").GetInt32()).IsEqualTo(30);
        await ErrorAsync("delete_project", new { organizationId, projectId, confirmName = "Personal" }, "default or last project");
        await ErrorAsync("set_audit_retention", new { organizationId, projectId, days = 0 }, "1 to 90");
        using var probeBefore = JsonDocument.Parse(await browser.GetStringAsync("/__test/reporting/probe"));
        var requestCount = probeBefore.RootElement.GetProperty("requests").GetArrayLength();
        await ErrorAsync("get_usage", new { organizationId = "foreign-organization", projectId }, "Organization is unavailable");
        await ErrorAsync("get_usage", new { organizationId, projectId = "foreign-project" }, "Project is unavailable");
        await ErrorAsync("get_usage", new { organizationId, projectId, databaseId = "foreign-database" }, "Database is unavailable");
        using var probeAfter = JsonDocument.Parse(await browser.GetStringAsync("/__test/reporting/probe"));
        await Assert.That(probeAfter.RootElement.GetProperty("requests").GetArrayLength()).IsEqualTo(requestCount);
        using var after = JsonDocument.Parse(await browser.GetStringAsync("/api/v1/dashboard/context"));
        await Assert.That(after.RootElement.GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo(selectedOrganization);
        await Assert.That(after.RootElement.GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(selectedProject);
        var selectedSettings = await CallAsync("get_project_settings", new { organizationId = selectedOrganization, projectId = selectedProject });
        await Assert.That(selectedSettings.GetProperty("auditRetentionDays").GetInt32()).IsEqualTo(90);

        async Task<JsonElement> CallAsync(string name, object arguments, bool useReplica = false)
        {
            var response = await HttpJourney.McpAsync(useReplica ? replica : browser, secret, "tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            var failed = result.TryGetProperty("isError", out var error) && error.GetBoolean();
            var details = failed ? await browser.GetStringAsync("/__test/reporting/probe") : "";
            await Assert.That(failed).IsFalse().Because(response + details);
            return result.GetProperty("structuredContent");
        }
        async Task ErrorAsync(string name, object arguments, string message)
        {
            var response = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            await Assert.That(result.GetProperty("isError").GetBoolean()).IsTrue();
            await Assert.That(result.GetProperty("content")[0].GetProperty("text").GetString()!).Contains(message);
        }
    }
}
