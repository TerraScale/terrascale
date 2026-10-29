using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    [Test]
    [Arguments("viewer")]
    [Arguments("member")]
    [Arguments("admin")]
    public async Task McpExplicitGrantPagesUseCurrentExactProjectAuthority(string role)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("mcp-grant-reader-" + role);
        var owner = platform.Principal("mcp-grant-owner");
        using var browser = platform.Client();
        using var replica = platform.Client(replica: true);
        using (var login = await HttpJourney.LoginAsync(browser, user.Email, user.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(browser, password: user.Password);
        var selected = (await McpCall(replica, secret, "get_context", new { })).GetProperty("selection");
        var own = selected.GetProperty("organizationId").GetString()!;
        var org = platform.Producer.AddOrganization("MCP explicit grants", owner.Subject);
        var project = platform.Producer.AddProject(org, "Exact project", owner.Subject);
        var sibling = platform.Producer.AddProject(org, "Hidden sibling", owner.Subject);
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        var expected = new List<string> { owner.Subject, user.Subject };
        foreach (var name in new[] { "grant-a", "grant-b" })
        {
            var target = platform.Principal(name);
            platform.Producer.AddCollaborator(org, target.Subject, project, "viewer");
            expected.Add(target.Subject);
        }
        var implied = platform.Principal("implied-only");
        platform.Producer.AddMember(org, implied.Subject, implied.Email, "admin");
        await platform.StoreAsync(new OrganizationRecord { Id = org });
        var version = await platform.VersionAsync(org);
        var path = "/v1/terrascale/organizations/" + org + "/projects/" + project + "/members";
        var scope = new { organizationId = org, projectId = project };
        var tools = (await HttpJourney.McpAsync(replica, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools");
        var tool = tools.EnumerateArray().Single(item => item.GetProperty("name").GetString() == "list_project_members");
        foreach (var field in new[] { "readOnlyHint", "idempotentHint", "openWorldHint" })
            await Assert.That(tool.GetProperty("annotations").GetProperty(field).GetBoolean()).IsTrue();
        await Assert.That(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()).IsFalse();
        foreach (var field in new[] { "organizationId", "projectId", "after" })
            await Assert.That(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty(field, out _)).IsTrue();
        foreach (var field in new[] { "organizationId", "projectId", "grants", "next" })
            await Assert.That(tool.GetProperty("outputSchema").GetProperty("properties").TryGetProperty(field, out _)).IsTrue();
        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 3; page++)
        {
            var result = await McpCall(replica, secret, "list_project_members", new { organizationId = org, projectId = project, after });
            await Assert.That(result.GetProperty("organizationId").GetString()).IsEqualTo(org);
            await Assert.That(result.GetProperty("projectId").GetString()).IsEqualTo(project);
            await Assert.That(result.GetRawText()).DoesNotContain(implied.Email).And.DoesNotContain(sibling);
            foreach (var row in result.GetProperty("grants").EnumerateArray())
            {
                seen.Add(row.GetProperty("userId").GetString()!);
                await Assert.That(row.GetProperty("role").GetString()).IsEqualTo(seen.Last() == user.Subject ? role : seen.Last() == owner.Subject ? "admin" : "viewer");
                await Assert.That(row.GetProperty("grantedAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.MinValue);
            }
            after = result.GetProperty("next").GetString();
            if (after is null) break;
        }
        await Assert.That(seen).IsEquivalentTo(expected);
        await Assert.That(platform.Producer.Count("GET", path, user.Subject)).IsEqualTo(2);
        foreach (var cursor in new[] { "", "bad\nvalue", new string('a', 513) })
            await McpDenied(replica, secret, "list_project_members", new { organizationId = org, projectId = project, after = cursor }, "The page cursor is invalid. Open the first page.");
        await Assert.That(platform.Producer.Count("GET", path, user.Subject)).IsEqualTo(2);
        foreach (var invalid in new object[] { new { organizationId = own, projectId = project }, new { organizationId = org, projectId = sibling },
            new { organizationId = org, projectId = "invalid-id" }, new { organizationId = "invalid-id", projectId = project }, new { projectId = project } })
            await McpDenied(replica, secret, "list_project_members", invalid);
        platform.Producer.RefuseNext("GET", path, HttpStatusCode.ServiceUnavailable, "unavailable", "Private provider detail");
        await McpDenied(replica, secret, "list_project_members", scope, "The organization provider could not complete this request. Reload it and try again.");
        // A browser cookie alone never authorizes the MCP transport.
        using (var cookieOnly = await browser.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } }))
            await Assert.That(cookieOnly.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        platform.Producer.RemoveGrant(org, user.Subject, project);
        await McpDenied(replica, secret, "list_project_members", scope);
        var context = await McpCall(replica, secret, "get_context", new { });
        await Assert.That(JsonElement.DeepEquals(context.GetProperty("selection"), selected)).IsTrue();
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
        await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);
        foreach (var resource in new[] { "members", "invitations", "ownership-transfer", "audit-events" })
            await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }
}
