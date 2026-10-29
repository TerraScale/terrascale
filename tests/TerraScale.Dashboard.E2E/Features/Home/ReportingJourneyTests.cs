using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Home;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class ReportingJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativeAnalyticsUsageSpendingAndSettingsDesktopAndMobile()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var flow = new BrowserFlow(browser, fixture.Origin, BrowserAudit.Output(fixture.Root, "reporting-journey"), 20000);
        await flow.RunAsync(async () =>
        {
            var suffix = Guid.NewGuid().ToString()[..8];
            var (ownerContext, ownerPage) = await flow.SessionAsync(1440, 900);
            var (guestContext, guestPage) = await flow.SessionAsync(390, 844);
            var ownerUser = new FlowUser("Reporting " + suffix, $"reporting-{suffix}@example.test", "e2e-password-" + Guid.NewGuid());
            var guestUser = new FlowUser("Reader " + suffix, $"report-reader-{suffix}@example.test", "e2e-password-" + Guid.NewGuid());
            await flow.SignupAsync(ownerPage, ownerUser);
            await flow.SignupAsync(guestPage, guestUser);
            await flow.GotoAsync(ownerPage, "/organization?newOrganization=organization");
            await ownerPage.Locator("#create-organization input[name=name]").FillAsync("Reporting company " + suffix);
            await flow.SubmitAsync(ownerPage, "#create-organization form", "Create organization");
            await flow.SubmitAsync(ownerPage, "#create-organization form", "Continue to checkout");
            await ownerPage.Locator("#create-organization input[name=name]").FillAsync("Measured project");
            await ownerPage.Locator("#create-organization select[name=regions]").SelectOptionAsync(["GRU", "SJC"]);
            await ownerPage.Locator("#create-organization select[name=region]").SelectOptionAsync("GRU");
            await ownerPage.Locator("#create-organization select[name=backupRegion]").SelectOptionAsync("SJC");
            await flow.SubmitAsync(ownerPage, "#create-organization form", "Create project");
            var original = await flow.MeAsync(ownerContext);
            var projectId = original.GetProperty("projectId").GetString()!;
            var orders = await DatabaseAsync("orders-" + suffix, ["GRU", "SJC"]);
            var archive = await DatabaseAsync("archive-" + suffix, ["SJC", "GRU"]);

            await flow.GotoAsync(ownerPage, "/analytics?database=" + orders + "&region=GRU&range=24h");
            await Assert.That(await Stat("#analytics-summary", "Requests").InnerTextAsync()).IsEqualTo("10");
            await Assert.That(await Stat("#analytics-summary", "Storage").InnerTextAsync()).IsEqualTo("4.1 kB");
            await Assert.That(await Stat("#analytics-summary", "Worst p95 latency").InnerTextAsync()).IsEqualTo("1.75 ms");
            await Assert.That(await ownerPage.Locator("main .ts-grid-2 > .ts-card").CountAsync()).IsEqualTo(7);
            await flow.ShotAsync(ownerPage, "analytics-desktop");
            var chart = ownerPage.Locator("main .ts-grid-2 .ts-chart").First;
            await chart.Locator(".ts-chart-column[tabindex=\"0\"]").FocusAsync();
            await ownerPage.Keyboard.PressAsync("Home");
            await Assert.That(await chart.Locator(".ts-chart-column").First.GetAttributeAsync("tabindex")).IsEqualTo("0");
            await ownerPage.Keyboard.PressAsync("End");
            await Assert.That(await chart.Locator(".ts-chart-column").Last.GetAttributeAsync("tabindex")).IsEqualTo("0");
            foreach (var range in new[] { "1h", "24h", "7d", "30d" })
            {
                var navigation = BrowserFlow.NavigationAsync(ownerPage);
                await ownerPage.Locator("main .ts-grid-2 .ts-card").First.GetByRole(AriaRole.Link, new() { Name = range, Exact = true }).ClickAsync();
                await navigation;
                await Assert.That(QueryHelpers.ParseQuery(new Uri(ownerPage.Url).Query)["range"].ToString()).IsEqualTo(range);
            }
            await ownerPage.Locator("#analytics-database").SelectOptionAsync(archive);
            await ApplyAnalyticsAsync();
            await Assert.That(await ownerPage.Locator("#analytics-region").InputValueAsync()).IsEqualTo("GRU").Because("native database changes keep a region the new database also uses");
            await Assert.That((await flow.MeAsync(ownerContext)).GetProperty("projectId").GetString()).IsEqualTo(projectId).Because("reporting filters do not save a new selected project");
            flow.Checks.Add("native Analytics range/database/region filters, measured counters/latest gauges and Datastar chart keyboard navigation");

            await flow.GotoAsync(ownerPage, "/analytics?database=" + orders);
            await ScenarioAsync("missing-latency");
            await flow.GotoAsync(ownerPage, "/analytics?database=" + orders);
            await Assert.That(await Stat("#analytics-summary", "Worst p95 latency").InnerTextAsync()).IsEqualTo("Not reported");
            var latency = ownerPage.Locator("main .ts-card").Filter(new() { Has = ownerPage.GetByRole(AriaRole.Heading, new() { Name = "Approximate request latency percentiles (ms)", Exact = true }) });
            await Assert.That(await latency.Locator(".ts-chart-marker").CountAsync()).IsEqualTo(0);
            await latency.GetByText("No samples for Approximate request latency percentiles (ms).", new() { Exact = true }).WaitForAsync();
            await ScenarioAsync("zero");
            await flow.GotoAsync(ownerPage, "/analytics?database=" + orders);
            await Assert.That(await Stat("#analytics-summary", "Requests").InnerTextAsync()).IsEqualTo("0");
            await Assert.That(await Stat("#analytics-summary", "Worst p95 latency").InnerTextAsync()).IsEqualTo("Not reported");
            foreach (var value in new[] { "foreign-project", "foreign-database", "foreign-region", "negative", "oversized", "offline" })
            {
                await ScenarioAsync(value);
                await flow.GotoAsync(ownerPage, "/analytics?database=" + orders);
                await ownerPage.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Analytics unavailable" }).WaitForAsync();
                await Assert.That(await ownerPage.Locator("#analytics-summary .ts-stat").CountAsync()).IsEqualTo(0).Because(value + " does not fabricate a zero total");
                await Assert.That(await ownerPage.Locator("select#analytics-range").IsVisibleAsync()).IsTrue().Because("unavailable charts must still allow a wider range");
            }
            await ScenarioAsync("received-only");
            await flow.GotoAsync(ownerPage, "/analytics?database=" + orders);
            await Assert.That(await Stat("#analytics-summary", "Replication sent").InnerTextAsync()).IsEqualTo("0 B");
            await Assert.That(await Stat("#analytics-summary", "Replication received").InnerTextAsync()).IsEqualTo("9 B");
            await ScenarioAsync("not-metered");
            await flow.GotoAsync(ownerPage, "/analytics");
            await ownerPage.GetByText("TerraBase has no analytics for this project or database yet. They start with its first database.", new() { Exact = true }).WaitForAsync();
            await ownerPage.Locator("#analytics-range").SelectOptionAsync("7d");
            await ApplyAnalyticsAsync();
            await Assert.That(QueryHelpers.ParseQuery(new Uri(ownerPage.Url).Query)["range"].ToString()).IsEqualTo("7d");
            await Assert.That(await ownerPage.Locator("[name=range]").CountAsync()).IsEqualTo(1);
            await ScenarioAsync("valid");
            await flow.GotoAsync(ownerPage, "/usage?project=" + projectId + "&database=" + orders + "&range=24h");
            await Assert.That(await Stat("#usage-summary", "Reads").InnerTextAsync()).IsEqualTo("12");
            await Assert.That(await Stat("#usage-summary", "Writes").InnerTextAsync()).IsEqualTo("7");
            await Assert.That(await Stat("#usage-summary", "Logical storage").InnerTextAsync()).IsEqualTo("4.1 kB");
            await Assert.That(await ownerPage.Locator("#replication tbody tr").CountAsync()).IsEqualTo(2);
            await flow.ShotAsync(ownerPage, "usage-desktop");
            foreach (var value in new[] { "no-period", "foreign-project", "foreign-database", "negative", "oversized", "offline", "unauthorized" })
            {
                await ScenarioAsync(value);
                await flow.GotoAsync(ownerPage, value == "foreign-project" ? "/usage" : "/usage?database=" + orders);
                await Assert.That(await Stat("#usage-summary", "Reads").CountAsync()).IsEqualTo(0).Because(value + " never presents lifetime reads as period usage");
                await Assert.That(await Stat("#usage-summary", "Writes").CountAsync()).IsEqualTo(0).Because(value + " never presents lifetime writes as period usage");
                if (value == "foreign-project") await Assert.That(await Stat("#usage-summary", "Logical storage").CountAsync()).IsEqualTo(0);
                else await Assert.That(await Stat("#usage-summary", "Logical storage").InnerTextAsync()).IsEqualTo("0 B").Because(value + " keeps the independently read empty database storage known");
            }
            await ScenarioAsync("valid");
            flow.Checks.Add("project-scoped analytics contract, tenant/scope/oversize failures and null-vs-zero observations through running routes");

            await flow.GotoAsync(ownerPage, "/usage?tab=settings");
            await ownerPage.GetByRole(AriaRole.Heading, new() { Name = "Spending and overages", Exact = true }).WaitForAsync();
            await Assert.That(await ownerPage.Locator("#spending-controls, #organization-spend-cap, #paid-excess").CountAsync()).IsEqualTo(0);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/usage/spending-controls", new() { ["spendCap"] = "50.25" }, 404);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/usage/internal-spending-cap", new() { ["spendCap"] = "50.25" }, 404);
            var persisted = (await (await ownerContext.APIRequest.GetAsync(flow.Origin + "/api/v1/dashboard/context")).JsonAsync())!.Value;
            var org = persisted.GetProperty("organizations").EnumerateArray().First(item => item.GetProperty("id").GetString() == original.GetProperty("organizationId").GetString());
            var project = org.GetProperty("projects").GetProperty(projectId);
            await Assert.That(org.TryGetProperty("spendCapCents", out _)).IsFalse();
            await Assert.That(project.TryGetProperty("spendCapCents", out _)).IsFalse();
            await Assert.That(project.GetProperty("databases").GetProperty(orders).TryGetProperty("spendCapCents", out _)).IsFalse();
            await flow.ShotAsync(ownerPage, "spending-desktop");
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/settings/audit-retention", new() { ["retentionDays"] = "0" }, 400);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/settings/audit-retention", new() { ["retentionDays"] = "30" });
            var provider = (await (await ownerContext.APIRequest.GetAsync(flow.Origin + "/__test/reporting/probe")).JsonAsync())!.Value;
            await Assert.That(provider.GetProperty("requests").EnumerateArray().Any(request => request.GetProperty("path").GetString()!.EndsWith("/audit-retention", StringComparison.Ordinal)))
                .IsFalse().Because("audit retention is the dashboard's setting; TerraBase's management retention does not know ts-auth organizations");
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/settings/backup-now", new() { ["databaseId"] = orders }, 503);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/settings/restore", new() { ["databaseId"] = orders, ["backupId"] = "demo-backup" }, 400);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/settings/delete-project", new() { ["projectId"] = projectId, ["confirmName"] = "Measured project" }, 409);
            flow.Checks.Add("customer spending setters absent, internal-only cap protected and dashboard audit-retention with honest backup/protected-delete gaps");

            await flow.GotoAsync(ownerPage, "/organization?tab=members");
            await ownerPage.Locator("#invite-email").FillAsync(guestUser.Email);
            await ownerPage.Locator("#invite-role").SelectOptionAsync("member");
            await flow.SubmitAsync(ownerPage, "#team-invite form", "Create invitation");
            var invitation = new Uri((await ownerPage.Locator("#invitation-link").GetAttributeAsync("href"))!).AbsolutePath;
            await flow.GotoAsync(guestPage, invitation);
            await flow.SubmitAsync(guestPage, "form[action$=\"/respond\"]", "Accept invitation");
            await flow.GotoAsync(guestPage, "/usage?tab=settings");
            await Assert.That(await guestPage.GetByRole(AriaRole.Button, new() { Name = "Save spending controls", Exact = true }).CountAsync()).IsEqualTo(0);
            await Assert.That(await guestPage.Locator("#organization-spend-cap, #paid-excess").CountAsync()).IsEqualTo(0);
            await flow.PostAsync(guestContext, guestPage, "/api/v1/dashboard/usage/spending-controls", new() { ["spendCap"] = "1.00" }, 404);
            await flow.PostAsync(guestContext, guestPage, "/api/v1/dashboard/settings/audit-retention", new() { ["retentionDays"] = "30" }, 403);
            await flow.ShotAsync(guestPage, "spending-member-mobile");
            await ownerPage.SetViewportSizeAsync(390, 844);
            foreach (var (path, name) in new[] { ("/analytics", "analytics-mobile"), ("/usage", "usage-mobile"), ("/usage?tab=settings", "spending-owner-mobile") })
            {
                await flow.GotoAsync(ownerPage, path);
                await flow.ShotAsync(ownerPage, name);
            }
            flow.Checks.Add("fresh member permissions and desktop/mobile WCAG 2.2 accessibility for measured reporting and honest spending availability");
            await Assert.That(flow.Errors).IsEmpty().Because("no browser or Datastar errors");
            flow.Gaps.Add("Producer replication contract has no historical bandwidth samples; the UI shows that state explicitly.");

            ILocator Stat(string card, string name) => ownerPage.Locator(card + " .ts-stat")
                .Filter(new() { Has = ownerPage.Locator("dt", new() { HasTextRegex = new Regex("^" + name + "$") }) }).Locator("dd.ts-stat-value");
            Task ScenarioAsync(string value) => flow.PostAsync(ownerContext, ownerPage, "/__test/reporting/scenario", new() { ["project"] = projectId, ["scenario"] = value }, 204);
            async Task ApplyAnalyticsAsync()
            {
                var navigation = BrowserFlow.NavigationAsync(ownerPage);
                await ownerPage.Locator("form:is([action=\"/analytics\"],[action^=\"/analytics?\"])").GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }).ClickAsync();
                await navigation;
            }
            async Task<string> DatabaseAsync(string name, string[] placement)
            {
                await flow.GotoAsync(ownerPage, "/databases");
                await ownerPage.GetByRole(AriaRole.Button, new() { Name = "New database", Exact = true }).First.ClickAsync();
                await ownerPage.Locator("#create-database[open]").WaitForAsync();
                await ownerPage.Locator("#database-name").FillAsync(name);
                await ownerPage.Locator("#database-home").SelectOptionAsync(placement[0]);
                foreach (var region in new[] { "GRU", "SJC" })
                    await ownerPage.Locator("#database-region input[value=\"" + region + "\"]").SetCheckedAsync(placement.Contains(region));
                await flow.SubmitAsync(ownerPage, "#create-database form", "Create database");
                return new Uri(ownerPage.Url).AbsolutePath.Split('/')[2];
            }
        }, probe: "/__test/reporting/probe");
    }

    [Test]
    public async Task ReportingRequiresSignInAndRejectsForeignScopesBeforeProviderCalls()
    {
        using var client = fixture.CreateClient();
        foreach (var path in new[] { "/analytics", "/usage" })
        {
            using var anonymous = await client.GetAsync(path);
            await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(anonymous.Headers.Location!.OriginalString.StartsWith("/login?returnTo=", StringComparison.Ordinal)).IsTrue();
        }
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var before = JsonDocument.Parse(await client.GetStringAsync("/__test/reporting/probe"));
        var requestsBefore = before.RootElement.GetProperty("requests").GetArrayLength();
        foreach (var path in new[] { "/analytics?project=foreign-project", "/analytics?database=foreign-database" })
        {
            using var foreign = await client.GetAsync(path);
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        // Valid region identities can be outside current placement (historical or newly admitted regions).
        using var invalidRegion = await client.GetAsync("/analytics?region=invalid_region");
        await Assert.That(invalidRegion.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var legacyBilling = await client.GetAsync("/usage?tab=billing&status=paid");
        await Assert.That(legacyBilling.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(legacyBilling.Headers.Location!.OriginalString).IsEqualTo("/organization?tab=billing&status=paid");
        using var noCsrf = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/usage/internal-spending-cap", new() { ["spendCap"] = "100.00" });
        await Assert.That(noCsrf.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var after = JsonDocument.Parse(await client.GetStringAsync("/__test/reporting/probe"));
        await Assert.That(after.RootElement.GetProperty("requests").GetArrayLength()).IsEqualTo(requestsBefore);
    }
}

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class AccountBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativeProfileOrganizationInvitationsRolesAuditAndLifecycleDesktopAndMobile()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var flow = new BrowserFlow(browser, fixture.Origin, BrowserAudit.Output(fixture.Root, "account-journey"), 15000);
        await flow.RunAsync(async () =>
        {
            var slug = Guid.NewGuid().ToString()[..8];
            var ownerUser = new FlowUser("Account QA " + slug, $"account-owner-{slug}@example.test", "qa-password-" + Guid.NewGuid());
            var guestUser = new FlowUser("Invite QA " + slug, $"account-guest-{slug}@example.test", "qa-password-" + Guid.NewGuid());
            var (ownerContext, ownerPage) = await flow.SessionAsync(1440, 900);
            var (guestContext, guestPage) = await flow.SessionAsync(390, 844);
            await flow.SignupAsync(ownerPage, ownerUser);
            await flow.SignupAsync(guestPage, guestUser);
            var original = await flow.MeAsync(ownerContext);
            var originalGuest = await flow.MeAsync(guestContext);
            await Assert.That(Text(original, "organizationId")).IsNotEqualTo(Text(originalGuest, "organizationId")).Because("isolated accounts have distinct actual scopes");
            await Assert.That(Text(original, "userId")).IsNotEqualTo(Text(originalGuest, "userId"));
            flow.Checks.Add("isolated owner/guest signup, email confirmation and persisted native free onboarding");

            await flow.GotoAsync(ownerPage, "/profile");
            await ownerPage.Locator("#displayName").FillAsync(ownerUser.Name + " Updated");
            await ownerPage.Locator("input[name=phoneNumber]").FillAsync("+55 11 99999-0000");
            await flow.SubmitAsync(ownerPage, "form:is([action=\"/api/v1/dashboard/profile\"],[action^=\"/api/v1/dashboard/profile?\"])", "Save profile");
            var saved = await flow.MeAsync(ownerContext);
            await Assert.That(Text(saved, "name")).IsEqualTo(ownerUser.Name + " Updated");
            await Assert.That(Text(saved, "phoneNumber")).IsEqualTo("+55 11 99999-0000");
            var locale = ownerPage.Locator("form:is([action=\"/api/v1/dashboard/profile/language\"],[action^=\"/api/v1/dashboard/profile/language?\"]) select[name=locale]");
            await locale.SelectOptionAsync("pt-BR");
            await flow.SubmitAsync(ownerPage, "form:is([action=\"/api/v1/dashboard/profile/language\"],[action^=\"/api/v1/dashboard/profile/language?\"])", "Save language");
            await Assert.That(Text(await flow.MeAsync(ownerContext), "locale")).IsEqualTo("pt-BR");
            await Assert.That(await ownerPage.Locator("html").GetAttributeAsync("lang")).IsEqualTo("en").Because("the saved language preference does not translate the English dashboard content");
            await locale.SelectOptionAsync("en");
            await flow.SubmitAsync(ownerPage, "form:is([action=\"/api/v1/dashboard/profile/language\"],[action^=\"/api/v1/dashboard/profile/language?\"])", "Save language");
            await flow.ShotAsync(ownerPage, "profile-desktop");
            await Assert.That((await ownerPage.TitleAsync()).StartsWith("Profile", StringComparison.Ordinal)).IsTrue().Because("profile metadata preserves the original page title");
            await flow.GotoAsync(ownerPage, "/profile?tab=sessions");
            await Assert.That(await ownerPage.Locator("#profile-sessions tbody tr").CountAsync()).IsGreaterThan(0);
            await ownerPage.GetByText("Current session", new() { Exact = true }).WaitForAsync();
            await flow.ShotAsync(ownerPage, "sessions-desktop");
            await flow.GotoAsync(ownerPage, "/profile?tab=security");
            await ownerPage.GetByRole(AriaRole.Heading, new() { Name = "Password", Exact = true }).WaitForAsync();
            await Assert.That(await ownerPage.GetByRole(AriaRole.Link, new() { Name = "Change password", Exact = true }).GetAttributeAsync("href"))
                .IsEqualTo("https://auth.terrascale.tech/ui/terrascale/reset").Because("password changes happen on ts-auth's hosted reset page");
            await flow.ShotAsync(ownerPage, "security-desktop");
            await flow.GotoAsync(ownerPage, "/profile?tab=security&error=" + Uri.EscapeDataString("Untrusted arbitrary error"));
            await Assert.That(await ownerPage.Locator("main").InnerTextAsync()).DoesNotContain("Untrusted arbitrary error");
            flow.Checks.Add("profile/name/phone/language persistence, actual sessions, ts-auth password reset link and bounded visible errors");

            await flow.GotoAsync(ownerPage, "/notifications?status=unread&page=999");
            await ownerPage.GetByText("You're all caught up", new() { Exact = true }).WaitForAsync();
            await Assert.That(await ownerPage.GetByRole(AriaRole.Button, new() { Name = "Mark all read", Exact = true }).IsDisabledAsync()).IsTrue();
            await Assert.That(await ownerPage.Locator("form[data-on\\:submit]").CountAsync()).IsEqualTo(0).Because("native notifications omit dialog-only event expressions");
            await flow.ShotAsync(ownerPage, "notifications-desktop");
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/notifications/missing/read", new(), 404);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/notifications/read-all");
            await ownerPage.Locator("details.ts-account summary").ClickAsync();
            await ownerPage.Locator("#ts-notifications-menu").ClickAsync();
            await ownerPage.Locator("#ts-notifications-dialog[open]").WaitForAsync();
            await ownerPage.Locator("#ts-notifications-body").GetByText("You're all caught up", new() { Exact = true }).WaitForAsync();
            await Assert.That(await ownerPage.Locator("#ts-notifications-body form").GetAttributeAsync("data-on:submit")).StartsWith("@post('/api/v1/notifications/read-all?organizationId=").And.EndsWith("', {contentType:'form'})");
            await flow.ShotAsync(ownerPage, "notifications-dialog-desktop", accessibility: false);
            await ownerPage.Locator("#ts-notifications-dialog").GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            flow.Checks.Add("empty notification filtering/paging, native mutation authorization and live Datastar modal patch");
            flow.Checks.Add("Empty new-account inbox checked here; NotificationBrowserJourneyTests separately covers seeded native per-item and Datastar read-all updates on desktop/mobile.");

            await flow.GotoAsync(ownerPage, "/organization?tab=billing");
            await flow.SubmitAsync(ownerPage, "[aria-labelledby=\"plan-starter\"] form", "Upgrade to Starter");
            await flow.GotoAsync(ownerPage, "/organization?tab=projects&newProject=1");
            await ownerPage.Locator("#create-project[open]").WaitForAsync();
            await ownerPage.Locator("#project-name").FillAsync("Disposable " + slug);
            await ownerPage.Locator("#project-regions").SelectOptionAsync(["GRU", "SJC"]);
            await ownerPage.Locator("#project-region").SelectOptionAsync("GRU");
            await ownerPage.Locator("#project-backup").SelectOptionAsync("SJC");
            await flow.SubmitAsync(ownerPage, "#create-project form", "Create project");
            var row = ownerPage.Locator("#projects tbody tr").Filter(new() { HasText = "Disposable " + slug });
            var projectId = await row.Locator(".ts-org-project-name .ts-mono").InnerTextAsync();
            await row.GetByRole(AriaRole.Button, new() { Name = "Rename", Exact = true }).ClickAsync();
            await ownerPage.Locator("#rename-project-" + projectId + " input[name=name]").FillAsync("Renamed " + slug);
            await flow.SubmitAsync(ownerPage, "#rename-project-" + projectId + " form", "Save project");
            await flow.SubmitAsync(ownerPage, "#projects tbody tr:has-text(\"Renamed " + slug + "\") form:is([action=\"/api/v1/dashboard/switch-project\"],[action^=\"/api/v1/dashboard/switch-project?\"])", "Select");
            await Assert.That(Text(await flow.MeAsync(ownerContext), "projectId")).IsEqualTo(projectId);
            await flow.ShotAsync(ownerPage, "projects-desktop");
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/settings/delete-project", new() { ["projectId"] = projectId, ["confirmName"] = "Wrong" }, 400);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/settings/delete-project", new() { ["projectId"] = projectId, ["confirmName"] = "Renamed " + slug });
            await flow.GotoAsync(ownerPage, "/organization?tab=projects");
            await Assert.That(await ownerPage.Locator("#projects tbody tr").Filter(new() { HasText = "Renamed " + slug }).CountAsync()).IsEqualTo(0);
            await Assert.That(Text(await flow.MeAsync(ownerContext), "projectId")).IsEqualTo(Text(original, "projectId"));
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/switch-project", new() { ["projectId"] = Text(originalGuest, "projectId")! }, 404);
            flow.Checks.Add("native project create/placement/rename/select, scoped rejection and confirmed empty-project deletion/fallback");

            await flow.GotoAsync(ownerPage, "/organization?newOrganization=organization");
            await ownerPage.Locator("#create-organization[open]").WaitForAsync();
            await ownerPage.Locator("#create-organization input[name=name]").FillAsync("Invite company " + slug);
            await flow.SubmitAsync(ownerPage, "#create-organization form", "Create organization");
            await ownerPage.GetByRole(AriaRole.Heading, new() { Name = "Set up billing", Exact = true }).WaitForAsync();
            var business = await flow.MeAsync(ownerContext);
            var businessId = Text(business, "organizationId");
            await Assert.That(businessId).IsNotEqualTo(Text(original, "organizationId"));
            await Assert.That(business.GetProperty("projectId").ValueKind).IsEqualTo(JsonValueKind.Null);
            await flow.SubmitAsync(ownerPage, "#create-organization form", "Continue to checkout");
            await flow.GotoAsync(ownerPage, "/organization");
            var declined = await InviteAsync();
            await flow.GotoAsync(guestPage, declined);
            await Assert.That((await flow.SubmitAsync(guestPage, "form[action$=\"/respond\"]", "Decline")).Status).IsEqualTo(303);
            await flow.GotoAsync(guestPage, declined, 404);
            var first = await InviteAsync();
            await flow.GotoAsync(ownerPage, first);
            await ownerPage.GetByRole(AriaRole.Alert).Filter(new() { HasText = "but you are signed in as" }).WaitForAsync();
            var (anonContext, anonPage) = await flow.SessionAsync(1280, 800);
            await flow.GotoAsync(anonPage, first);
            await anonPage.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }).WaitForAsync();
            await SigninAsync(anonPage, ownerUser, first);
            await anonPage.GetByRole(AriaRole.Alert).Filter(new() { HasText = "but you are signed in as" }).WaitForAsync();
            // The browser stops on ts-auth's hosted sign-in page (stood in here) instead of the stand-in signing straight back in.
            var switched = await flow.SubmitAsync(anonPage, "form[action$=\"/switch-account\"]", "Sign out and switch account");
            await Assert.That(switched.Status).IsEqualTo(303);
            await Assert.That((await anonContext.APIRequest.GetAsync(flow.Origin + "/api/v1/auth/me")).Status).IsEqualTo(401).Because("switch-account ends the independently created dashboard session");
            await Assert.That(Text(await flow.MeAsync(ownerContext), "userId")).IsEqualTo(Text(original, "userId")).Because("switch-account leaves the separate owner session usable");
            await Assert.That(switched.Headers["location"]).IsEqualTo("/login").Because("switch-account signs out at ts-auth's end-session (the demo host's end-session answers /login), so ts-auth shows its sign-in instead of the same account");
            await SigninAsync(anonPage, guestUser, first);
            await anonPage.GetByRole(AriaRole.Button, new() { Name = "Accept invitation", Exact = true }).WaitForAsync();
            await flow.GotoAsync(ownerPage, "/organization?tab=members");
            await flow.SubmitAsync(ownerPage, "#team-invitations tbody tr:has-text(\"Pending\") form[action*=\"/resend\"]", "Resend");
            var replacement = new Uri((await ownerPage.Locator("#invitation-link").GetAttributeAsync("href"))!).AbsolutePath;
            await Assert.That(replacement).IsNotEqualTo(first);
            await flow.GotoAsync(guestPage, first, 404);
            await flow.GotoAsync(guestPage, replacement);
            await flow.ShotAsync(guestPage, "invitation-mobile");
            await flow.SubmitAsync(guestPage, "form[action$=\"/respond\"]", "Accept invitation");
            await Assert.That(Text(await flow.MeAsync(guestContext), "organizationId")).IsEqualTo(businessId);
            await Assert.That(Text(await flow.MeAsync(guestContext), "role")).IsEqualTo("member");
            await flow.GotoAsync(guestPage, replacement, 404);
            await flow.GotoAsync(guestPage, "/organization?tab=members");
            await flow.ShotAsync(guestPage, "member-access-mobile");
            await flow.GotoAsync(ownerPage, "/organization?tab=members");
            var guestId = Text(await flow.MeAsync(guestContext), "userId")!;
            var roleForm = "#team-members form[action*=\"/" + Uri.EscapeDataString(guestId) + "/role\"]";
            await ownerPage.Locator("#team-members tbody tr").Filter(new() { HasText = guestUser.Email }).Locator("select[name=role]").SelectOptionAsync("viewer");
            await Assert.That((await flow.SubmitAsync(ownerPage, roleForm, "Save role")).Status).IsEqualTo(303);
            await Assert.That(Text(await flow.MeAsync(guestContext), "role")).IsEqualTo("viewer");
            await flow.PostAsync(guestContext, guestPage, "/api/v1/dashboard/organization/rename", new() { ["name"] = "Forbidden viewer" }, 403);
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/access/members/" + Uri.EscapeDataString(guestId) + "/role", new() { ["role"] = "unsupported" }, 422);
            await Assert.That(Text(await flow.MeAsync(guestContext), "role")).IsEqualTo("viewer").Because("an unsupported role never changes membership");
            await ownerPage.Locator("#team-members tbody tr").Filter(new() { HasText = guestUser.Email }).Locator("select[name=role]").SelectOptionAsync("member");
            await flow.SubmitAsync(ownerPage, roleForm, "Save role");
            await Assert.That(Text(await flow.MeAsync(guestContext), "role")).IsEqualTo("member");
            await flow.GotoAsync(guestPage, "/organization");
            await flow.PostAsync(guestContext, guestPage, "/api/v1/dashboard/organization/rename", new() { ["name"] = "Forbidden" }, 403);
            await flow.ShotAsync(ownerPage, "members-desktop");
            await ownerPage.SetViewportSizeAsync(390, 844);
            await flow.ShotAsync(ownerPage, "members-owner-mobile");
            await ownerPage.SetViewportSizeAsync(1440, 900);
            flow.Checks.Add("invitation decline/wrong account/anonymous prompt, native session-isolated switch-account and invited-user reauthentication, revoke-and-resend, consumed-token rejection, Viewer read-only authority and unsupported role denial before mutation");

            await flow.GotoAsync(ownerPage, "/organization?tab=members");
            await RequestTransferAsync(ownerPage, guestId);
            await flow.GotoAsync(ownerPage, "/organization?tab=danger");
            await ownerPage.GetByText("Ownership transfer pending", new() { Exact = true }).WaitForAsync();
            await flow.SubmitAsync(ownerPage, "form:is([action=\"/api/v1/dashboard/organization/members/ownership/cancel\"],[action^=\"/api/v1/dashboard/organization/members/ownership/cancel?\"])", "Cancel transfer");
            await RequestTransferAsync(ownerPage, guestId);
            await flow.GotoAsync(guestPage, "/organization?tab=danger");
            await flow.SubmitAsync(guestPage, "form:is([action=\"/api/v1/dashboard/organization/members/ownership/confirm\"],[action^=\"/api/v1/dashboard/organization/members/ownership/confirm?\"])", "Accept ownership");
            await Assert.That(Text(await flow.MeAsync(guestContext), "role")).IsEqualTo("owner");
            await Assert.That(Text(await flow.MeAsync(ownerContext), "role")).IsEqualTo("admin");
            await flow.PostAsync(ownerContext, ownerPage, "/api/v1/dashboard/organization/members/ownership", new() { ["userID"] = guestId }, 403);
            await RequestTransferAsync(guestPage, Text(original, "userId")!);
            await flow.GotoAsync(ownerPage, "/organization?tab=danger");
            await flow.SubmitAsync(ownerPage, "form:is([action=\"/api/v1/dashboard/organization/members/ownership/confirm\"],[action^=\"/api/v1/dashboard/organization/members/ownership/confirm?\"])", "Accept ownership");
            await Assert.That(Text(await flow.MeAsync(ownerContext), "role")).IsEqualTo("owner");
            await Assert.That(Text(await flow.MeAsync(guestContext), "role")).IsEqualTo("admin");
            flow.Checks.Add("native ownership start/cancel/recipient confirmation and reverse transfer with fresh role authorization");

            await flow.GotoAsync(ownerPage, "/organization?tab=members");
            await ownerPage.Locator("#team-members tbody tr").Filter(new() { HasText = guestUser.Email }).Locator("select[name=role]").SelectOptionAsync("member");
            await flow.SubmitAsync(ownerPage, roleForm, "Save role");
            await Assert.That(Text(await flow.MeAsync(guestContext), "role")).IsEqualTo("member");
            await ownerPage.Locator("#member-search").FillAsync(ownerUser.Email);
            await ownerPage.Locator("#access-filter-form summary").ClickAsync();
            var filtered = ownerPage.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == "/organization"
                && response.Request.Headers.TryGetValue("datastar-request", out var datastar) && datastar == "true");
            await ownerPage.Locator("#access-filter-form").GetByRole(AriaRole.Button, new() { Name = "Organization owner", Exact = true }).ClickAsync();
            await Assert.That((await filtered).Headers["content-type"]).StartsWith("text/event-stream");
            await ownerPage.WaitForFunctionAsync("() => document.querySelector('#member-role-filter-value')?.textContent === 'Organization owner' && document.querySelectorAll('#team-members tbody tr').length === 1");
            await Assert.That(await ownerPage.Locator("#access-filter-form input[name=role]").InputValueAsync()).IsEqualTo("owner");
            await flow.GotoAsync(ownerPage, "/organization?tab=members&q=" + Uri.EscapeDataString(ownerUser.Email) + "&role=owner&page=999");
            await Assert.That(await ownerPage.Locator("#team-members tbody tr").CountAsync()).IsEqualTo(1);
            await flow.GotoAsync(ownerPage, "/organization?tab=audit");
            await Assert.That((await ownerPage.TitleAsync()).StartsWith("Organization", StringComparison.Ordinal)).IsTrue().Because("organization metadata preserves the original page title");
            await Assert.That(await ownerPage.Locator("dialog[id=create-project]").CountAsync()).IsEqualTo(0).Because("audit does not include unrelated project mutation forms");
            await Assert.That(await ownerPage.Locator("#audit-range").InputValueAsync()).IsEqualTo("7d");
            await ownerPage.Locator("#audit-search").FillAsync("invitation");
            var applied = BrowserFlow.NavigationAsync(ownerPage);
            await ownerPage.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await applied;
            await Assert.That(await ownerPage.Locator("#recent-activity tbody").InnerTextAsync()).Contains("invitation");
            await flow.ShotAsync(ownerPage, "audit-desktop");
            await ownerPage.Locator("summary[aria-label=\"Export audit events\"]").ClickAsync();
            var jsonHref = await ownerPage.GetByRole(AriaRole.Link, new() { Name = "Export JSON", Exact = true }).GetAttributeAsync("href");
            var exported = await ownerContext.APIRequest.GetAsync(flow.Origin + jsonHref);
            await Assert.That(exported.Status).IsEqualTo(200);
            var exportData = (await exported.JsonAsync())!.Value;
            await Assert.That(exportData.GetProperty("count").GetInt32()).IsGreaterThan(0);
            await Assert.That(exportData.GetProperty("events").EnumerateArray().All(item => Text(item, "action")!.Contains("invitation", StringComparison.Ordinal))).IsTrue();
            var csvHref = await ownerPage.GetByRole(AriaRole.Link, new() { Name = "Export CSV", Exact = true }).GetAttributeAsync("href");
            await Assert.That(await (await ownerContext.APIRequest.GetAsync(flow.Origin + csvHref)).TextAsync()).StartsWith("auditEventId,occurredAt,actor,actorType");
            await flow.GotoAsync(guestPage, "/organization?tab=audit", 403);
            await Assert.That((await guestContext.APIRequest.GetAsync(flow.Origin + "/audit/export?format=json")).Status).IsEqualTo(403);
            flow.Checks.Add("member query/role/page filters, 7d audit defaults, action search, actual JSON/CSV export and member export authorization");

            await flow.GotoAsync(guestPage, "/organization?tab=danger");
            await guestPage.GetByRole(AriaRole.Button, new() { Name = "Leave organization", Exact = true }).First.ClickAsync();
            await flow.SubmitAsync(guestPage, "#leave-organization form", "Leave organization");
            await Assert.That(Text(await flow.MeAsync(guestContext), "organizationId")).IsEqualTo(Text(originalGuest, "organizationId"));
            await flow.PostAsync(guestContext, guestPage, "/api/v1/dashboard/switch-organization", new() { ["organizationId"] = businessId! }, 404);
            var repeatInvite = await InviteAsync();
            await flow.GotoAsync(guestPage, repeatInvite);
            await flow.SubmitAsync(guestPage, "form[action$=\"/respond\"]", "Accept invitation");
            await flow.GotoAsync(ownerPage, "/organization?tab=members");
            await ownerPage.Locator("#team-members tbody tr").Filter(new() { HasText = guestUser.Email }).GetByRole(AriaRole.Button, new() { Name = "Remove access", Exact = true }).First.ClickAsync();
            await flow.SubmitAsync(ownerPage, "[id=\"remove-member-" + guestId + "\"] form", "Remove access");
            await Assert.That((await guestContext.APIRequest.GetAsync(flow.Origin + "/api/v1/auth/me")).Status).IsEqualTo(401).Because("member removal revokes the actual dashboard session");
            await SigninAsync(guestPage, guestUser, "/organization");
            await Assert.That(Text(await flow.MeAsync(guestContext), "organizationId")).IsEqualTo(Text(originalGuest, "organizationId"));
            await flow.PostAsync(guestContext, guestPage, "/api/v1/dashboard/switch-organization", new() { ["organizationId"] = businessId! }, 404);
            var revokedInvite = await InviteAsync();
            await flow.GotoAsync(ownerPage, "/organization?tab=members");
            await flow.SubmitAsync(ownerPage, "#team-invitations tbody tr:has-text(\"Pending\") form[action*=\"/revoke\"]", "Cancel");
            await flow.GotoAsync(guestPage, revokedInvite, 404);
            flow.Checks.Add("native member removal causes live scope fallback; owner invitation revoke invalidates the scoped token");

            await flow.GotoAsync(ownerPage, "/organization?tab=danger");
            await ownerPage.GetByRole(AriaRole.Button, new() { Name = "Organization deletion", Exact = true }).ClickAsync();
            await ownerPage.Locator("#deletion-confirmation").FillAsync("Wrong");
            await Assert.That((await flow.SubmitAsync(ownerPage, "#delete-organization form", "Delete organization")).Status).IsEqualTo(422);
            await flow.GotoAsync(ownerPage, "/organization?tab=danger");
            await ownerPage.GetByRole(AriaRole.Button, new() { Name = "Organization deletion", Exact = true }).ClickAsync();
            await ownerPage.Locator("#deletion-confirmation").FillAsync("Invite company " + slug);
            await flow.SubmitAsync(ownerPage, "#delete-organization form", "Delete organization");
            await ownerPage.GetByRole(AriaRole.Status).Filter(new() { HasText = "Organization deleted." }).WaitForAsync();
            await Assert.That(Text(await flow.MeAsync(ownerContext), "organizationId")).IsEqualTo(Text(original, "organizationId"));
            var remaining = (await (await ownerContext.APIRequest.GetAsync(flow.Origin + "/api/v1/dashboard/context")).JsonAsync())!.Value;
            await Assert.That(remaining.GetProperty("organizations").EnumerateArray().Any(item => Text(item, "id") == businessId)).IsFalse();
            flow.Checks.Add("native business member leave, actual session revocation after removal, and confirmed organization deletion moves the owner to the personal organization");

            await ownerPage.SetViewportSizeAsync(390, 844);
            foreach (var (path, key) in new[] { ("/profile", "profile-mobile"), ("/profile?tab=sessions", "sessions-mobile"), ("/profile?tab=security", "security-mobile"), ("/organization", "organization-mobile"), ("/organization?tab=danger", "organization-danger-mobile"), ("/organization?tab=projects", "projects-mobile"), ("/notifications", "notifications-mobile") })
            {
                await flow.GotoAsync(ownerPage, path);
                await flow.ShotAsync(ownerPage, key);
            }
            await Assert.That(flow.Errors).IsEmpty().Because("no browser runtime errors including Datastar expressions");
            flow.Gaps.Add("Queued organization deletion preserves data pending the external worker completion contract.");

            async Task<string> InviteAsync()
            {
                await flow.GotoAsync(ownerPage, "/organization?tab=members");
                await ownerPage.Locator("#invite-email").FillAsync(guestUser.Email);
                await ownerPage.Locator("#invite-role").SelectOptionAsync("member");
                await Assert.That((await flow.SubmitAsync(ownerPage, "#team-invite form", "Create invitation")).Status).IsEqualTo(200);
                var link = new Uri((await ownerPage.Locator("#invitation-link").GetAttributeAsync("href"))!);
                await Assert.That(link.GetLeftPart(UriPartial.Authority)).IsEqualTo(flow.Origin);
                return link.AbsolutePath;
            }
            async Task SigninAsync(IPage page, FlowUser user, string invitationPath)
            {
                await BrowserAudit.SignInAsync(page, new Uri(flow.Origin), invitationPath, user.Email, user.Password);
                await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == flow.Origin + invitationPath, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            async Task RequestTransferAsync(IPage page, string recipient)
            {
                await flow.GotoAsync(page, "/organization?tab=danger");
                await page.GetByRole(AriaRole.Button, new() { Name = "Transfer ownership", Exact = true }).ClickAsync();
                await page.Locator("#new-owner").SelectOptionAsync(recipient);
                await flow.SubmitAsync(page, "#transfer-ownership form", "Request transfer");
            }
        });

        static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();
    }
}

file sealed record FlowUser(string Name, string Email, string Password);

// Shared session/navigation/form helpers for the multi-user reporting and account journeys.
file sealed class BrowserFlow(IBrowser browser, Uri origin, string output, float timeout) : IAsyncDisposable
{
    private readonly List<IBrowserContext> contexts = [];
    private IPage? active;
    public string Origin { get; } = origin.ToString().TrimEnd('/');
    public ConcurrentQueue<string> Errors { get; } = new();
    public List<string> Checks { get; } = [];
    public List<string> Gaps { get; } = [];

    public async Task<(IBrowserContext Context, IPage Page)> SessionAsync(int width, int height)
    {
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height }, AcceptDownloads = true });
        contexts.Add(context);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(timeout);
        page.PageError += (_, error) => Errors.Enqueue(page.Url + ": " + error);
        return (context, page);
    }

    public async Task<IResponse> GotoAsync(IPage page, string path, int status = 200)
    {
        active = page;
        var response = await page.GotoAsync(Origin + path, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assert.That(response!.Status).IsEqualTo(status).Because(path);
        if (status == 200) await Assert.That(await page.Locator("main").CountAsync()).IsGreaterThan(0).Because("full page body exists after navigation");
        return response;
    }

    // Waits like the former Promise.all(waitForResponse, waitForNavigation, click): the native POST and the page it lands on.
    public async Task<IResponse> SubmitAsync(IPage page, string selector, string button)
    {
        active = page;
        var form = page.Locator(selector);
        var path = new Uri(new Uri(Origin), await form.GetAttributeAsync("action")).AbsolutePath;
        var posted = page.WaitForResponseAsync(value => new Uri(value.Url).AbsolutePath == path && value.Request.Method == "POST");
        var navigation = NavigationAsync(page);
        await form.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync();
        var response = await posted;
        await navigation;
        if (path.EndsWith("/respond", StringComparison.Ordinal) || path.EndsWith("/switch-account", StringComparison.Ordinal))
        {
            await Assert.That(response.Request.Headers.GetValueOrDefault("origin")).IsEqualTo(Origin).Because("invitation native POST retains its actual origin");
            await Assert.That(response.Request.Headers.GetValueOrDefault("referer") ?? "").DoesNotContain("/invitations/").Because("invitation token path is absent from the referrer");
        }
        return response;
    }

#pragma warning disable CS0612, CS0618 // Playwright marks WaitForNavigation racy, but it is the exact "next navigation" wait the journeys started before clicking.
    public static Task NavigationAsync(IPage page) => page.WaitForNavigationAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
#pragma warning restore CS0612, CS0618

    public async Task<JsonElement> MeAsync(IBrowserContext context)
    {
        var response = await context.APIRequest.GetAsync(Origin + "/api/v1/auth/me");
        await Assert.That(response.Status).IsEqualTo(200);
        return (await response.JsonAsync())!.Value;
    }

    public async Task<IAPIResponse> PostAsync(IBrowserContext context, IPage page, string path, Dictionary<string, string>? form = null, int status = 303)
    {
        var data = context.APIRequest.CreateFormData();
        data.Set("_csrf", await page.Locator("input[name=_csrf]").First.InputValueAsync());
        foreach (var (key, value) in form ?? new()) data.Set(key, value);
        var response = await context.APIRequest.PostAsync(Origin + path, new() { Form = data, MaxRedirects = 0 });
        await Assert.That(response.Status).IsEqualTo(status).Because(path);
        return response;
    }

    public async Task ShotAsync(IPage page, string name, bool accessibility = true)
    {
        active = page;
        await page.EvaluateAsync("() => document.fonts.ready");
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide });
        await Assert.That(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 2")).IsTrue().Because(name + " has no horizontal body overflow");
        if (accessibility) await BrowserAudit.AssertAccessibleAsync(page, "WCAG 2.2 AA on " + name);
    }

    // Hosted ts-auth sign-up, then free onboarding.
    public async Task SignupAsync(IPage page, FlowUser user)
    {
        active = page;
        await Assert.That((await BrowserAudit.SignInAsync(page, new Uri(Origin), "/onboarding", user.Email, user.Password, signUp: true))!.Status).IsEqualTo(200);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Review your account information", Exact = true }).WaitForAsync();
        await page.Locator("#displayName").FillAsync(user.Name);
        await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Save and continue");
        await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Continue on Free");
        await page.Locator("#projectName").FillAsync(user.Name + " Project");
        await page.Locator("#region").SelectOptionAsync("GRU");
        await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Create project and open Dashboard");
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == Origin + "/");
    }

    // Writes report.json, and on failure the active page's screenshot, HTML and optional provider probe.
    public async Task RunAsync(Func<Task> journey, string? probe = null)
    {
        try
        {
            await journey();
            await ReportAsync(true, null);
            Console.WriteLine("PASS: " + string.Join("; ", Checks));
        }
        catch (Exception error)
        {
            await ReportAsync(false, error.ToString());
            Console.Error.WriteLine(error);
            if (active is { IsClosed: false } page)
            {
                if (probe is not null) await File.WriteAllTextAsync(Path.Combine(output, "failure-provider.json"), await (await page.Context.APIRequest.GetAsync(Origin + probe)).TextAsync());
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, "failure.png"), FullPage = true });
                await File.WriteAllTextAsync(Path.Combine(output, "failure.html"), await page.ContentAsync());
                Console.Error.WriteLine("Failed at " + page.Url);
            }
            throw;
        }
    }

    private Task ReportAsync(bool passed, string? failure) => File.WriteAllTextAsync(Path.Combine(output, "report.json"),
        JsonSerializer.Serialize(new { passed, @base = Origin, checks = Checks, gaps = Gaps, browserErrors = Errors, failure }, new JsonSerializerOptions { WriteIndented = true }));

    public async ValueTask DisposeAsync()
    {
        foreach (var context in contexts) await context.CloseAsync();
    }
}
