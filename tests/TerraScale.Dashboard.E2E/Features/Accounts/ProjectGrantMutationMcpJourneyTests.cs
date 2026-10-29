using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test]
        [Arguments("viewer", "")]
        [Arguments("member", "")]
        [Arguments("admin", "")]
        [Arguments("admin", "viewer")]
        public async Task McpGrantChangesUseLiveProjectAdminWithoutOrganizationPromotion(string role, string organizationRole)
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("mcp-grant-admin");
            var owner = platform.Principal("mcp-grant-owner");
            var target = platform.Principal("mcp-grant-target");
            using var browser = platform.Client();
            using var replica = platform.Client(replica: true);
            using (var login = await HttpJourney.LoginAsync(browser, user.Email, user.Password))
            {
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var secret = await HttpJourney.CreatePatAsync(browser, password: user.Password);
            var selected = (await McpCall(replica, secret, "get_context", new { })).GetProperty("selection");
            var org = platform.Producer.AddOrganization("MCP grant changes", owner.Subject);
            // Creation alone must not preserve admin authority after demotion or removal.
            var project = platform.Producer.AddProject(org, "Exact project", user.Subject);
            var sibling = platform.Producer.AddProject(org, "Sibling project", owner.Subject);
            platform.Producer.AddCollaborator(org, user.Subject, project, role);
            platform.Producer.AddMember(org, target.Subject, target.Email, "viewer");
            if (organizationRole.Length > 0)
            {
                platform.Producer.AddMember(org, user.Subject, user.Email, organizationRole);
            }

            await platform.StoreAsync(new OrganizationRecord { Id = org });
            // Hydrate an organization member's normal projection before the mutation baseline.
            await McpCall(replica, secret, "list_project_members", new { organizationId = org, projectId = project });
            var discovered = (await McpCall(replica, secret, "get_context", new { })).GetProperty("organizations").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == org);
            if (organizationRole.Length == 0)
            {
                await Assert.That(discovered.GetProperty("role").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
            else
            {
                await Assert.That(discovered.GetProperty("role").GetString()).IsEqualTo(organizationRole);
            }

            await Assert.That(discovered.GetProperty("canManage").GetBoolean()).IsFalse();
            await McpDenied(replica, secret, "update_member_role", new { organizationId = org, projectId = project, userId = target.Subject, role = "admin" });
            var version = await platform.VersionAsync(org);
            var publications = platform.Producer.PlanPublications(org);
            var path = "/v1/terrascale/organizations/" + org + "/projects/" + project + "/members/";
            object Set(string id, string newRole) => new { organizationId = org, projectId = project, userId = id, role = newRole };
            object Remove(string id) => new { organizationId = org, projectId = project, userId = id };
            var tools = (await HttpJourney.McpAsync(replica, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools");
            foreach (var name in new[] { "set_project_member_role", "remove_project_member" })
            {
                var tool = tools.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
                await Assert.That(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()).IsTrue();
                await Assert.That(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()).IsFalse();
                await Assert.That(tool.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean()).IsEqualTo(name == "set_project_member_role");
                await Assert.That(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("userId", out _)).IsTrue();
            }
            if (role != "admin")
            {
                await McpDenied(replica, secret, "set_project_member_role", Set(target.Subject, "admin"));
                await McpDenied(replica, secret, "remove_project_member", Remove(user.Subject));
                await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
            }
            else
            {
                foreach (var invalid in new[] { (Id: "not-a-ulid", Role: "viewer"), (Id: target.Subject, Role: "owner"), (Id: target.Subject, Role: "") })
                {
                    await McpDenied(replica, secret, "set_project_member_role", Set(invalid.Id, invalid.Role));
                }

                await McpDenied(replica, secret, "remove_project_member", Remove("not-a-ulid"));
                foreach (var scope in new[] { (Org: org, Project: sibling), (Org: selected.GetProperty("organizationId").GetString()!, Project: project),
                    (Org: org, Project: "invalid-id"), (Org: "invalid-id", Project: project) })
                {
                    await McpDenied(replica, secret, "set_project_member_role", new { organizationId = scope.Org, projectId = scope.Project, userId = target.Subject, role = "admin" });
                    await McpDenied(replica, secret, "remove_project_member", new { organizationId = scope.Org, projectId = scope.Project, userId = user.Subject });
                }
                await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
                await McpDenied(replica, secret, "set_project_member_role", Set(AccountService.NewId(), "member"),
                    "The project or target user is unavailable. New grants require an existing organization member; otherwise ask an organization admin about a project invitation.");
                platform.Producer.RefuseNext("PUT", path + target.Subject, HttpStatusCode.ServiceUnavailable, "unavailable", "Private provider detail");
                await McpDenied(replica, secret, "set_project_member_role", Set(target.Subject, "admin"),
                    "The grant change could not be confirmed. Reload project grants before trying again.");
                var granted = await McpCall(replica, secret, "set_project_member_role", Set(target.Subject, "admin"));
                await Assert.That(granted.GetProperty("organizationId").GetString()).IsEqualTo(org);
                await Assert.That(granted.GetProperty("projectId").GetString()).IsEqualTo(project);
                await Assert.That(granted.GetProperty("grant").GetProperty("userId").GetString()).IsEqualTo(target.Subject);
                await Assert.That(granted.GetProperty("grant").GetProperty("role").GetString()).IsEqualTo("admin");
                await Assert.That(platform.Producer.Bodies("PUT", path + target.Subject).Last()).IsEqualTo("{\"role\":\"admin\"}");
                granted = await McpCall(replica, secret, "set_project_member_role", Set(target.Subject, "member"));
                await Assert.That(granted.GetProperty("grant").GetProperty("role").GetString()).IsEqualTo("member");
                var removed = await McpCall(replica, secret, "remove_project_member", Remove(target.Subject));
                await Assert.That(removed.GetProperty("removed").GetBoolean()).IsTrue();
                await Assert.That(removed.GetProperty("organizationId").GetString()).IsEqualTo(org);
                await Assert.That(removed.GetProperty("projectId").GetString()).IsEqualTo(project);
                await Assert.That(removed.GetProperty("userId").GetString()).IsEqualTo(target.Subject);
                await Assert.That(removed.GetProperty("effect").GetString()).Contains("Organization-implied access and independent service keys remain");
                await Assert.That(platform.Producer.Count("DELETE", path + target.Subject, user.Subject)).IsEqualTo(1);
                await Assert.That(platform.Producer.Role(org, target.Subject)).IsEqualTo("viewer");
                await McpCall(replica, secret, "set_project_member_role", Set(user.Subject, "viewer"));
                var mutations = platform.Producer.Mutations(org, user.Subject);
                await McpDenied(replica, secret, "set_project_member_role", Set(target.Subject, "admin"));
                await McpDenied(replica, secret, "remove_project_member", Remove(target.Subject));
                await McpCall(replica, secret, "list_project_members", new { organizationId = org, projectId = project });
                await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(mutations);
                platform.Producer.AddCollaborator(org, user.Subject, project, "admin");
                await McpCall(replica, secret, "remove_project_member", Remove(user.Subject));
                mutations = platform.Producer.Mutations(org, user.Subject);
                await McpDenied(replica, secret, "set_project_member_role", Set(target.Subject, "admin"));
                await McpDenied(replica, secret, "remove_project_member", Remove(target.Subject));
                if (organizationRole.Length == 0)
                {
                    await McpDenied(replica, secret, "list_project_members", new { organizationId = org, projectId = project });
                }
                else
                {
                    await McpCall(replica, secret, "list_project_members", new { organizationId = org, projectId = project });
                }

                await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(mutations);
                await Assert.That(platform.Producer.Count("PUT", path + user.Subject, user.Subject)).IsEqualTo(1);
                await Assert.That(platform.Producer.Count("DELETE", path + user.Subject, user.Subject)).IsEqualTo(1);
                await Assert.That(platform.Producer.Count("PUT", path + target.Subject, user.Subject)).IsEqualTo(3);
                // One unknown-target refusal, one outage, three target changes and two self changes; no other writes.
                await Assert.That(mutations).IsEqualTo(7);
            }
            var context = await McpCall(replica, secret, "get_context", new { });
            await Assert.That(JsonElement.DeepEquals(context.GetProperty("selection"), selected)).IsTrue();
            await Assert.That(platform.Producer.Role(org, user.Subject)).IsEqualTo(organizationRole.Length == 0 ? null : organizationRole);
            await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
            await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(publications);
            if (organizationRole.Length == 0)
            {
                foreach (var resource in new[] { "members", "invitations", "ownership-transfer", "audit-events" })
                {
                    await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
                }
            }

            await Assert.That(platform.Producer.Failures).IsEmpty();
        }

        [Test]
        [Arguments("member")]
        [Arguments("admin")]
        public async Task McpRemovingExplicitSelfGrantRetainsHigherImpliedAccess(string impliedRole)
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("mcp-implied-grant");
            using var browser = platform.Client();
            using (var login = await HttpJourney.LoginAsync(browser, user.Email, user.Password))
            {
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var secret = await HttpJourney.CreatePatAsync(browser, password: user.Password);
            var selected = (await McpCall(browser, secret, "get_context", new { })).GetProperty("selection");
            var org = selected.GetProperty("organizationId").GetString()!;
            var project = selected.GetProperty("projectId").GetString()!;
            platform.Producer.AddMember(org, user.Subject, user.Email, impliedRole);
            await McpCall(browser, secret, "list_project_members", new { });
            var version = await platform.VersionAsync(org);
            var publications = platform.Producer.PlanPublications(org);
            var mutations = platform.Producer.Mutations(org, user.Subject);
            await McpCall(browser, secret, "set_project_member_role", new { userId = user.Subject, role = "viewer" });
            await CheckRole(impliedRole);
            platform.Producer.AddCollaborator(org, user.Subject, project, "admin");
            await McpCall(browser, secret, "remove_project_member", new { userId = user.Subject });
            await CheckRole(impliedRole);
            var page = await McpCall(browser, secret, "list_project_members", new { });
            await Assert.That(page.GetProperty("grants").GetArrayLength()).IsEqualTo(0);
            if (impliedRole == "member")
            {
                await McpDenied(browser, secret, "set_project_member_role", new { userId = user.Subject, role = "admin" });
            }

            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(mutations + 2);
            await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
            await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(publications);
            await Assert.That(platform.Producer.Failures).IsEmpty();

            async Task CheckRole(string expected)
            {
                var context = await McpCall(browser, secret, "get_context", new { });
                await Assert.That(JsonElement.DeepEquals(context.GetProperty("selection"), selected)).IsTrue();
                var visible = context.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == org);
                await Assert.That(visible.GetProperty("projects")[0].GetProperty("role").GetString()).IsEqualTo(expected);
            }
        }
    }
}
