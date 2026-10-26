using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

// Share the existing producer's canonical role-null grants, not a second authorization model.
public sealed partial class TsAuthPlatformJourneyTests
{
    [Test]
    [Arguments("viewer")]
    [Arguments("member")]
    [Arguments("admin")]
    public async Task McpProjectGrantsDiscoverAndSelectExactProjectsWithoutOrganizationAuthority(string role)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("mcp-project-" + role);
        var owner = platform.Principal("mcp-project-owner");
        using var browser = platform.Client();
        using var replica = platform.Client(replica: true);
        using (var login = await HttpJourney.LoginAsync(browser, user.Email, user.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(browser, password: user.Password);
        var original = (await McpCall(replica, secret, "get_context", new { })).GetProperty("selection");
        var ownOrg = original.GetProperty("organizationId").GetString()!;
        var org = platform.Producer.AddOrganization("MCP collaborator workspace", owner.Subject);
        var project = platform.Producer.AddProject(org, "Granted project", owner.Subject);
        var sibling = platform.Producer.AddProject(org, "Private sibling", owner.Subject);
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        var saved = new OrganizationRecord { Id = org, Kind = "business", PlanId = "private-plan", BillingStatus = "private-billing" };
        saved.Projects[project] = new() { Id = project };
        saved.Projects[sibling] = new() { Id = sibling, IsDefault = true };
        await platform.StoreAsync(saved);
        var organizationVersion = await platform.VersionAsync(org);
        var database = AccountService.NewId();
        platform.SeedDatabase(new(database, project, "granted-db", DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));

        var context = await McpCall(replica, secret, "get_context", new { });
        var visible = context.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == org);
        foreach (var field in new[] { "kind", "status", "role", "planId", "planName", "billingStatus", "deletionRequested" })
            await Assert.That(visible.GetProperty(field).ValueKind).IsEqualTo(JsonValueKind.Null).Because(field);
        await Assert.That(visible.GetProperty("canManage").GetBoolean()).IsFalse();
        await Assert.That(visible.GetProperty("canProvision").GetBoolean()).IsFalse();
        await Assert.That(visible.GetProperty("projects").GetArrayLength()).IsEqualTo(1);
        await Assert.That(visible.GetProperty("projects")[0].GetProperty("role").GetString()).IsEqualTo(role);
        await Assert.That(visible.GetProperty("projects")[0].GetProperty("canWriteProjectData").GetBoolean()).IsEqualTo(role != "viewer");
        await Assert.That(context.GetProperty("user").GetProperty("platformAdmin").ValueKind).IsEqualTo(JsonValueKind.Null);
        foreach (var hidden in new[] { sibling, "private-plan", "private-billing", "ownerUserId", "members", "invitations", "auditEvents" })
            await Assert.That(visible.GetRawText()).DoesNotContain(hidden);

        // organizationId-only falls back to the first permitted project when the default is inaccessible.
        var databases = await McpCall(replica, secret, "list_databases", new { organizationId = org });
        await Assert.That(databases.GetProperty("databases")[0].GetProperty("id").GetString()).IsEqualTo(database);
        await McpDenied(replica, secret, "list_databases", new { projectId = project }, "Project is unavailable.");
        await McpDenied(replica, secret, "list_databases", new { organizationId = ownOrg, projectId = project }, "Project is unavailable.");
        await McpDenied(replica, secret, "list_databases", new { organizationId = org, projectId = sibling }, "Project is unavailable.");
        await McpDenied(replica, secret, "list_databases", new { organizationId = org, projectId = AccountService.NewId() }, "Project is unavailable.");
        var unchanged = await McpCall(replica, secret, "get_context", new { });
        await Assert.That(JsonElement.DeepEquals(unchanged.GetProperty("selection"), original)).IsTrue();

        var selected = await McpCall(replica, secret, "select_organization", new { organizationId = org });
        await Assert.That(selected.GetProperty("projectId").GetString()).IsEqualTo(project);
        await McpCall(replica, secret, "list_databases", new { });
        await McpCall(replica, secret, "list_databases", new { projectId = project });
        await McpCall(replica, secret, "select_organization", new { organizationId = ownOrg });
        selected = await McpCall(replica, secret, "select_project", new { projectId = project });
        await Assert.That(selected.GetProperty("organizationId").GetString()).IsEqualTo(org);
        await Assert.That(selected.GetProperty("projectId").GetString()).IsEqualTo(project);
        var persisted = await McpCall(browser, secret, "get_context", new { });
        await Assert.That(JsonElement.DeepEquals(persisted.GetProperty("selection"), selected)).IsTrue();
        await McpDenied(replica, secret, "select_project", new { projectId = sibling });

        var another = platform.Producer.AddProject(org, "Another permitted project", owner.Subject);
        platform.Producer.AddCollaborator(org, user.Subject, another, role);
        var anotherDatabase = AccountService.NewId();
        platform.SeedDatabase(new(anotherDatabase, another, "another-db", DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));
        await McpCall(replica, secret, "select_project", new { projectId = another });
        foreach (var scope in new object[] { new { }, new { organizationId = org } })
        {
            var scoped = await McpCall(replica, secret, "list_databases", scope);
            await Assert.That(scoped.GetProperty("databases")[0].GetProperty("id").GetString()).IsEqualTo(anotherDatabase);
        }
        await McpCall(replica, secret, "list_databases", new { organizationId = org, projectId = project });
        persisted = await McpCall(replica, secret, "get_context", new { });
        await Assert.That(persisted.GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(another);
        await McpCall(replica, secret, "select_project", new { projectId = project });

        foreach (var name in new[] { "get_organization", "get_billing", "list_plans", "list_invoices", "list_members", "list_invitations", "list_audit_events",
            "get_usage", "list_api_keys",
            "auth_get_overview", "auth_list_users", "auth_list_sessions", "auth_list_clients" })
            await McpDenied(replica, secret, name, new { organizationId = org, projectId = project });
        var mutations = new (string Name, object Arguments)[]
        {
            ("rename_organization", new { name = "Denied" }),
            ("create_project", new { name = "Denied" }),
            ("invite_member", new { email = "denied@example.test", role = "admin" }),
            ("update_member_role", new { userId = owner.Subject, role = "viewer" }),
            ("remove_member", new { userId = owner.Subject }),
            ("request_ownership_transfer", new { userId = user.Subject }),
            ("confirm_ownership_transfer", new { }),
            ("request_organization_deletion", new { confirmName = "MCP collaborator workspace" }),
            ("leave_organization", new { }),
            ("change_plan", new { plan = "free" }),
            ("create_database", new { name = "denied", regions = new[] { "GRU", "SFO" } }),
            ("auth_remove_provider", new { provider = "google" })
        };
        foreach (var mutation in mutations)
            await McpDenied(replica, secret, mutation.Name, mutation.Arguments);
        var rename = new { organizationId = org, projectId = project, name = "Renamed granted project" };
        if (role == "admin") await McpCall(replica, secret, "update_project", rename);
        else await McpDenied(replica, secret, "update_project", rename);
        context = await McpCall(replica, secret, "get_context", new { });
        visible = context.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == org);
        await Assert.That(visible.GetProperty("role").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(visible.GetProperty("canManage").GetBoolean()).IsFalse();
        await Assert.That(visible.GetProperty("projects").EnumerateArray().Single(item => item.GetProperty("id").GetString() == project).GetProperty("name").GetString())
            .IsEqualTo(role == "admin" ? rename.name : "Granted project");
        var renamePath = "/v1/terrascale/organizations/" + org + "/projects/" + project;
        await Assert.That(platform.Producer.Count("PATCH", renamePath, user.Subject)).IsEqualTo(role == "admin" ? 1 : 0);
        if (role == "admin") await Assert.That(platform.Producer.Bodies("PATCH", renamePath)).IsEquivalentTo(new[] { "{\"name\":\"Renamed granted project\"}" });
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(organizationVersion);
        foreach (var resource in new[] { "members", "ownership-transfer", "invitations", "audit-events" })
            await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(role == "admin" ? 1 : 0);
        await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);

        // Every PAT request re-reads grants, including after the token and selection have been used.
        platform.Producer.RemoveGrant(org, user.Subject, project);
        platform.Producer.RemoveGrant(org, user.Subject, another);
        await McpDenied(replica, secret, "list_databases", new { organizationId = org, projectId = project });
        await McpDenied(replica, secret, "select_project", new { projectId = project });
        context = await McpCall(replica, secret, "get_context", new { });
        await Assert.That(context.GetProperty("organizations").EnumerateArray().Any(item => item.GetProperty("id").GetString() == org)).IsFalse();
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        platform.Producer.RequireMfa(org);
        await McpDenied(replica, secret, "list_databases", new { organizationId = org, projectId = project });
        await McpDenied(replica, secret, "select_organization", new { organizationId = org });
        await McpDenied(replica, secret, "select_project", new { projectId = project });
        platform.Producer.Enroll(user.Subject, 10);
        await platform.ProjectionAsync(org, current => current.Projects[project].Status = "inactive");
        await McpDenied(replica, secret, "list_databases", new { organizationId = org, projectId = project });
        await platform.ProjectionAsync(org, current => current.Archived = true);
        await McpDenied(replica, secret, "select_organization", new { organizationId = org });
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    private static async Task<JsonElement> McpCall(HttpClient client, string secret, string name, object arguments)
    {
        var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        var result = rpc.GetProperty("result");
        await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(rpc.ToString());
        return result.GetProperty("structuredContent");
    }

    private static async Task McpDenied(HttpClient client, string secret, string name, object arguments, string? message = null)
    {
        var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        await Assert.That(rpc.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue().Because(name);
        if (message is not null)
            await Assert.That(rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()).IsEqualTo(message);
    }
}
