using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AccountMcpJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task NativePatPreservesSourceSelectionNotificationAndSafeOrganizationContractsAcrossReplicas()
        {
            await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            var persona = scenario.Personas.Seeded;
            using var login = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var replicaLogin = await HttpJourney.LoginAsync(replica, persona.Email, persona.Password);
            await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var secret = await HttpJourney.CreatePatAsync(browser, "TUnit source account contracts", persona.Password);
            var context = await CallAsync("get_context", new { });
            var selected = context.GetProperty("selection");
            var organizationId = selected.GetProperty("organizationId").GetString()!;
            var projectId = selected.GetProperty("projectId").GetString()!;
            var personal = context.GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("kind").GetString() == "personal");
            var personalProject = personal.GetProperty("projects")[0].GetProperty("id").GetString()!;

            using var manifest = await ReferenceFixtures.ReadManifestAsync(fixture.Root);
            var original = manifest.RootElement.GetProperty("mcp").GetProperty("toolsList").EnumerateArray()
                .SelectMany(page => page.GetProperty("result").GetProperty("tools").EnumerateArray())
                .ToDictionary(tool => tool.GetProperty("name").GetString()!);
            var discovery = await HttpJourney.McpAsync(browser, secret, "tools/list", new { });
            var tools = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
            foreach (var name in new[] { "get_organization", "create_organization", "select_organization", "select_project", "list_notifications", "mark_notification_read", "mark_all_notifications_read" })
            {
                foreach (var schema in new[] { "inputSchema", "outputSchema" })
                {
                    await Assert.That(Properties(tools[name].GetProperty(schema))).IsEquivalentTo(Properties(original[name].GetProperty(schema))).Because(name + " " + schema);
                }
            }

            var organization = await CallAsync("get_organization", new { organizationId, projectId });
            await Assert.That(Properties(organization)).IsEquivalentTo(original["get_organization"].GetProperty("outputSchema").GetProperty("required")
                .EnumerateArray().Select(field => field.GetString()!).Order(StringComparer.Ordinal).ToArray());
            await Assert.That(organization.GetProperty("projects").ValueKind).IsEqualTo(JsonValueKind.Array);
            await Assert.That(organization.GetProperty("memberCount").GetInt32()).IsEqualTo(3);
            await Assert.That(organization.GetRawText().Contains("tokenHash", StringComparison.OrdinalIgnoreCase)).IsFalse();
            await Assert.That(organization.TryGetProperty("members", out _)).IsFalse();
            await Assert.That(organization.TryGetProperty("auditEvents", out _)).IsFalse();

            var changed = await CallAsync("select_project", new { projectId = personalProject });
            await Assert.That(changed.GetProperty("organizationId").GetString()).IsEqualTo(personal.GetProperty("id").GetString());
            await Assert.That(changed.GetProperty("projectId").GetString()).IsEqualTo(personalProject);
            var empty = await CallAsync("list_notifications", new { });
            await Assert.That(empty.GetProperty("total").GetInt32()).IsEqualTo(0);
            var inbox = await CallAsync("list_notifications", new { organizationId, projectId, page = 100 });
            await Assert.That(inbox.GetProperty("total").GetInt32()).IsEqualTo(4);
            await Assert.That(inbox.GetProperty("matching").GetInt32()).IsEqualTo(4);
            await Assert.That(inbox.GetProperty("unread").GetInt32()).IsEqualTo(2);
            await Assert.That(inbox.GetProperty("page").GetInt32()).IsEqualTo(1);
            var unread = await CallAsync("list_notifications", new { organizationId, projectId, status = "unread" });
            await Assert.That(unread.GetProperty("matching").GetInt32()).IsEqualTo(2);
            var notificationId = unread.GetProperty("notifications")[0].GetProperty("id").GetString()!;
            var denied = await HttpJourney.McpAsync(replica, secret, "tools/call", new
            { name = "mark_notification_read", arguments = new { organizationId = personal.GetProperty("id").GetString(), projectId = personalProject, notificationId } });
            await Assert.That(denied.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            var marked = await CallAsync("mark_notification_read", new { organizationId, projectId, notificationId });
            await Assert.That(marked.GetProperty("marked").GetInt32()).IsEqualTo(1);
            marked = await CallAsync("mark_notification_read", new { organizationId, projectId, notificationId });
            await Assert.That(marked.GetProperty("marked").GetInt32()).IsEqualTo(0);
            marked = await CallAsync("mark_all_notifications_read", new { organizationId, projectId });
            await Assert.That(marked.GetProperty("marked").GetInt32()).IsEqualTo(1);
            marked = await CallAsync("mark_all_notifications_read", new { organizationId, projectId });
            await Assert.That(marked.GetProperty("marked").GetInt32()).IsEqualTo(0);
            context = await CallAsync("get_context", new { });
            await Assert.That(context.GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(personalProject);

            var restored = await CallAsync("select_organization", new { organizationId });
            await Assert.That(restored.GetProperty("organizationId").GetString()).IsEqualTo(organizationId);
            await Assert.That(restored.GetProperty("projectId").GetString()).IsEqualTo(projectId);
            using var native = await browser.GetAsync("/notifications?status=unread");
            await Assert.That(native.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await native.Content.ReadAsStringAsync()).Contains("No unread notifications");
            var created = await CallAsync("create_organization", new { name = "TUnit MCP account " + Guid.NewGuid().ToString("N")[..8] });
            await Assert.That(Properties(created)).IsEquivalentTo(Properties(original["create_organization"].GetProperty("outputSchema")));
            await Assert.That(created.GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            await Assert.That(created.GetProperty("canProvision").GetBoolean()).IsFalse();

            async Task<JsonElement> CallAsync(string name, object arguments)
            {
                var rpc = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name, arguments });
                var result = rpc.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(rpc.ToString());
                return result.GetProperty("structuredContent");
            }
        }

        private static string[] Properties(JsonElement value)
        {
            return (value.TryGetProperty("properties", out var properties) ? properties : value).EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray();
        }
    }
}
