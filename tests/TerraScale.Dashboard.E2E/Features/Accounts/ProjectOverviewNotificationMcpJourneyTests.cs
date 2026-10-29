using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    [Test]
    [Arguments("viewer")]
    [Arguments("member")]
    [Arguments("admin")]
    public async Task ProjectOverviewAndNotificationsUseLiveExactGrantsWithoutOrganizationMetadata(string role)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("mcp-overview-" + role);
        var owner = platform.Principal("mcp-overview-owner");
        using var browser = platform.Client();
        using var replica = platform.Client(replica: true);
        using (var login = await HttpJourney.LoginAsync(browser, user.Email, user.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(browser, password: user.Password);
        var selected = (await McpCall(replica, secret, "get_context", new { })).GetProperty("selection");
        var ownOrg = selected.GetProperty("organizationId").GetString()!;
        var ownProject = selected.GetProperty("projectId").GetString()!;
        var org = platform.Producer.AddOrganization("Project notifications workspace", owner.Subject);
        var project = platform.Producer.AddProject(org, "Selected notification project", owner.Subject);
        var sibling = platform.Producer.AddProject(org, "Other granted project", owner.Subject);
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        platform.Producer.AddCollaborator(org, user.Subject, sibling, role);
        var saved = new OrganizationRecord { Id = org };
        saved.Projects[project] = new() { Id = project };
        saved.Projects[sibling] = new() { Id = sibling };
        var siblingDatabase = AccountService.NewId();
        saved.Projects[sibling].Databases[siblingDatabase] = new() { Id = siblingDatabase, ProjectId = sibling, Region = "SFO", Regions = ["SFO"] };
        await platform.StoreAsync(saved);
        var version = await platform.VersionAsync(org);
        var global = Note("Global account notice");
        var exact = Note("Exact project notice", org, project);
        var own = Note("Personal owner allowance", ownOrg, type: "allowance-warning");
        var hidden = new[] { Note("ORG_SENTINEL", org), Note("SIBLING_SENTINEL", org, sibling),
            Note("ORPHAN_SENTINEL", "", project), Note("MISMATCH_SENTINEL", ownOrg, project), Note("ALLOWANCE_SENTINEL", org, project, "allowance-warning"),
            Note("GLOBAL_ALLOWANCE_SENTINEL", type: "allowance-warning") };
        await platform.AccountAsync(user.Subject, account =>
        {
            account.OnboardingCompleted = true;
            account.Notifications = [global, exact, own, .. hidden];
        });
        var scope = new { organizationId = org, projectId = project };
        var query = "?organizationId=" + org + "&projectId=" + project;
        var overview = await McpCall(replica, secret, "get_overview", scope);
        await Assert.That(overview.GetProperty("organizationMembers").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(overview.GetProperty("activeCredentials").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(overview.GetProperty("totalNotifications").GetInt32()).IsEqualTo(2);
        var inbox = await McpCall(replica, secret, "list_notifications", scope);
        await Assert.That(inbox.GetProperty("notifications").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray())
            .IsEquivalentTo(new[] { global.Id, exact.Id });
        foreach (var item in hidden)
            await McpDenied(replica, secret, "mark_notification_read", new { organizationId = org, projectId = project, notificationId = item.Id }, "Notification is unavailable.");
        await McpCall(replica, secret, "mark_notification_read", new { organizationId = org, projectId = project, notificationId = global.Id });
        using (var page = await browser.GetAsync("/" + query))
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Requires organization membership");
        }
        using (var regions = await browser.GetAsync("/overview/regions" + query))
        {
            await Assert.That(regions.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await regions.Content.ReadAsStringAsync()).DoesNotContain("SFO");
        }
        using (var page = await browser.GetAsync("/notifications" + query))
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await page.Content.ReadAsStringAsync();
            await Assert.That(html).Contains(global.Title).And.Contains(exact.Title);
            await Assert.That(WebUtility.HtmlDecode(html)).Contains("/api/v1/notifications/" + exact.Id + "/read" + query);
            foreach (var item in hidden) await Assert.That(html).DoesNotContain(item.Title);
        }
        var csrf = await HttpJourney.CsrfAsync(browser, "/notifications" + query);
        using (var denied = await HttpJourney.PostFormAsync(browser, "/api/v1/notifications/" + hidden[0].Id + "/read" + query, new() { ["_csrf"] = csrf }))
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        browser.DefaultRequestHeaders.Add("Datastar-Request", "true");
        using (var marked = await HttpJourney.PostFormAsync(browser, "/api/v1/notifications/" + exact.Id + "/read" + query,
            new() { ["_csrf"] = csrf, ["status"] = "unread" }))
        {
            await Assert.That(marked.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await marked.Content.ReadAsStringAsync()).Contains("No unread notifications");
        }
        browser.DefaultRequestHeaders.Remove("Datastar-Request");
        await platform.AccountAsync(user.Subject, account => account.Notifications.Add(Note("Next project notice", org, project)));
        var all = await McpCall(replica, secret, "mark_all_notifications_read", scope);
        await Assert.That(all.GetProperty("marked").GetInt32()).IsEqualTo(1);
        inbox = await McpCall(replica, secret, "list_notifications", scope);
        await Assert.That(inbox.GetProperty("unread").GetInt32()).IsEqualTo(0);
        var context = await McpCall(replica, secret, "get_context", new { });
        await Assert.That(JsonElement.DeepEquals(context.GetProperty("selection"), selected)).IsTrue();
        platform.Producer.RemoveGrant(org, user.Subject, project);
        foreach (var name in new[] { "get_overview", "list_notifications", "mark_all_notifications_read" })
            await McpDenied(replica, secret, name, scope);
        await McpDenied(replica, secret, "mark_notification_read", new { organizationId = org, projectId = project, notificationId = exact.Id });
        using (var revoked = await browser.GetAsync("/notifications" + query))
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // Organization membership still permits an org-only inbox after its last project is removed.
        platform.Producer.RemoveProject(ownOrg, ownProject);
        inbox = await McpCall(replica, secret, "list_notifications", new { organizationId = ownOrg });
        await Assert.That(inbox.GetProperty("notifications").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray())
            .IsEquivalentTo(new[] { global.Id, own.Id });
        all = await McpCall(replica, secret, "mark_all_notifications_read", new { organizationId = ownOrg });
        await Assert.That(all.GetProperty("marked").GetInt32()).IsEqualTo(1);
        using (var page = await browser.GetAsync("/notifications?organizationId=" + ownOrg))
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var page = await browser.GetAsync("/?organizationId=" + ownOrg))
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
        await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);
        await Assert.That(platform.Producer.Failures).IsEmpty();

        static NotificationRecord Note(string title, string organizationId = "", string projectId = "", string type = "info") =>
            new() { Id = AccountService.NewId(), Title = title, OrganizationId = organizationId, ProjectId = projectId, Type = type, CreatedAt = DateTimeOffset.UtcNow };
    }
}
