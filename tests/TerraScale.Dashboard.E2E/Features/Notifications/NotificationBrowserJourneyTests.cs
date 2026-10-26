using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Notifications;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class NotificationBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task NativeUnreadFormAndLiveDialogPersistWithinTheirAccountAndWorkspace(int width, int height)
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        // Datastar aborts each finished SSE fetch and Chromium can drop that body, so assert the body Playwright fetched for the browser.
        var bodies = new ConcurrentDictionary<IRequest, string>();
        await page.RouteAsync("**/notifications/dialog**", FulfillFromServerAsync);
        await page.RouteAsync("**/api/v1/notifications/read-all**", FulfillFromServerAsync);
        page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        using var replica = scenario.CreateClient(replica: true);
        using var unrelated = scenario.CreateClient();
        var persona = scenario.Personas.Seeded;
        using var replicaLogin = await HttpJourney.LoginAsync(replica, persona.Email, persona.Password);
        await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var unrelatedLogin = await HttpJourney.LoginAsync(unrelated, scenario.Personas.Routes.Email, scenario.Personas.Routes.Password);
        await Assert.That(unrelatedLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(replica, "TUnit notification browser " + width, persona.Password);
        var selected = (await CallAsync("get_context", new { })).GetProperty("selection");
        var organizationId = selected.GetProperty("organizationId").GetString()!;
        var projectId = selected.GetProperty("projectId").GetString()!;

        await BrowserAudit.SignInAsync(page, scenario.Origin, email: persona.Email, password: persona.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        await NavigateAsync("/notifications?status=unread");
        await Assert.That(await page.Locator("main .ts-notification-unread").CountAsync()).IsEqualTo(2);
        await Assert.That(await page.Locator("main .ts-notifications-summary").InnerTextAsync()).Contains("2 unread");
        await Assert.That(await page.Locator("main .ts-notifications-summary").InnerTextAsync()).Contains("4 total");
        await Assert.That(await page.Locator("main form[data-on\\:submit]").CountAsync()).IsEqualTo(0)
            .Because("The full inbox submits ordinary native forms; only the dialog uses Datastar mutation expressions.");
        await AssertBadgeAsync(2);
        var initial = await InboxAsync();
        await Assert.That(initial.GetProperty("unread").GetInt32()).IsEqualTo(2);
        await Assert.That(initial.GetProperty("total").GetInt32()).IsEqualTo(4);

        var first = page.Locator("main .ts-notification-unread form").First;
        var action = (await first.GetAttributeAsync("action"))!;
        var notificationId = action.Split('/')[4];
        using var badCsrf = await HttpJourney.PostFormAsync(replica, action, new() { ["_csrf"] = new string('0', 64) });
        await Assert.That(badCsrf.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var unrelatedToken = await HttpJourney.CsrfAsync(unrelated, "/notifications");
        using var foreign = await HttpJourney.PostFormAsync(unrelated, action, new() { ["_csrf"] = unrelatedToken });
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound)
            .Because("A notification ID from another account never admits a native mutation.");
        await Assert.That((await InboxAsync()).GetProperty("unread").GetInt32()).IsEqualTo(2)
            .Because("Both rejected requests leave the seeded unread records intact.");

        var native = await page.RunAndWaitForResponseAsync(
            () => first.GetByRole(AriaRole.Button, new() { Name = "Mark read", Exact = true }).ClickAsync(),
            response => response.Request.IsNavigationRequest && response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == action.Split('?')[0]);
        await Assert.That(native.Status).IsEqualTo(303);
        await page.WaitForFunctionAsync("() => document.querySelectorAll('main .ts-notification-unread').length === 1 && document.querySelector('main .ts-notifications-summary')?.textContent.includes('1 unread')");
        await Assert.That(new Uri(BrowserAudit.WithoutWorkspace(page.Url)).PathAndQuery).IsEqualTo("/notifications?status=unread");
        await Assert.That(await page.Locator("main .ts-notification-unread").CountAsync()).IsEqualTo(1);
        await AssertBadgeAsync(1);
        var afterNative = await InboxAsync();
        await Assert.That(afterNative.GetProperty("unread").GetInt32()).IsEqualTo(1);
        await Assert.That(afterNative.GetProperty("notifications").EnumerateArray().Single(item => item.GetProperty("id").GetString() == notificationId)
            .GetProperty("status").GetString()).IsEqualTo("Read")
            .Because("The other replica's native-issued PAT observes the browser's committed per-item form mutation.");

        async Task<IResponse> OpenDialogAsync() => await page.RunAndWaitForResponseAsync(async () =>
        {
            if (width < 768)
            {
                await OpenNavigationAsync();
                await page.Locator("#ts-notifications-topbar").ClickAsync();
            }
            else
            {
                await page.Locator("details.ts-account summary").ClickAsync();
                await page.Locator("#ts-notifications-menu").ClickAsync();
            }
        }, response => response.Request.Method == "GET" && new Uri(response.Url).AbsolutePath == "/notifications/dialog");
        var opened = await OpenDialogAsync();
        await AssertPatchAsync(opened);
        if (width < 768) await Assert.That(await page.Locator("#ts-navigation #ts-notifications-topbar .ts-nav-count").IsVisibleAsync()).IsTrue()
            .Because("The unread badge is visible on the bell inside the open mobile drawer.");
        var dialog = page.Locator("#ts-notifications-dialog[open]");
        await dialog.WaitForAsync();
        await dialog.Locator(".ts-notification-list article").Nth(3).WaitForAsync();
        await Assert.That(await dialog.Locator(".ts-notification-list article").CountAsync()).IsEqualTo(4);
        await Assert.That(await dialog.Locator(".ts-notification-unread").CountAsync()).IsEqualTo(1);
        var filtered = await page.RunAndWaitForResponseAsync(
            () => dialog.GetByRole(AriaRole.Button, new() { Name = "Unread", Exact = false }).ClickAsync(),
            response => response.Request.Method == "GET" && new Uri(response.Url).AbsolutePath == "/notifications/dialog" &&
                QueryHelpers.ParseQuery(new Uri(response.Url).Query)["status"] == "unread");
        await AssertPatchAsync(filtered);
        await page.WaitForFunctionAsync("() => document.querySelector('#ts-notifications-body input[name=status]')?.value === 'unread' && document.querySelectorAll('#ts-notifications-body .ts-notification-list article').length === 1");
        await Assert.That(await dialog.Locator(".ts-notification-list article").CountAsync()).IsEqualTo(1);
        await Assert.That(await dialog.Locator("form:is([action='/api/v1/notifications/read-all'],[action^='/api/v1/notifications/read-all?'])").GetAttributeAsync("data-on:submit"))
            .IsEqualTo("@post('/api/v1/notifications/read-all', {contentType:'form'})");
        var pageUrl = page.Url;
        var marked = await page.RunAndWaitForResponseAsync(
            () => dialog.GetByRole(AriaRole.Button, new() { Name = "Mark all read", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/notifications/read-all");
        await AssertPatchAsync(marked);
        await page.WaitForFunctionAsync("() => document.querySelector('#ts-notifications-body .ts-notifications-summary')?.textContent.includes('0 unread') && document.querySelector('#ts-notifications-topbar')?.getAttribute('aria-label') === 'Notifications' && document.querySelector('#ts-notifications-menu')?.getAttribute('aria-label') === 'Notifications'");
        await Assert.That(page.Url).IsEqualTo(pageUrl).Because("The populated modal mutates and patches in place without a page navigation.");
        await Assert.That(await dialog.IsVisibleAsync()).IsTrue();
        await Assert.That(await dialog.Locator(".ts-notification-list article").CountAsync()).IsEqualTo(0);
        await Assert.That(await dialog.GetByText("No unread notifications in this scope.", new() { Exact = true }).IsVisibleAsync()).IsTrue();
        await Assert.That(await dialog.GetByRole(AriaRole.Button, new() { Name = "Mark all read", Exact = true }).IsDisabledAsync()).IsTrue();
        await AssertBadgeAsync(0);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Assert.That(await page.Locator("#ts-notifications-dialog").GetAttributeAsync("open")).IsNull();
        await page.WaitForFunctionAsync("selector => document.activeElement === document.querySelector(selector)",
            width < 768 ? "#ts-notifications-topbar" : "details.ts-account summary");
        if (width < 768)
        {
            await Assert.That(await page.Locator("#ts-nav-toggle").GetAttributeAsync("aria-expanded")).IsEqualTo("true")
                .Because("The drawer stays open while its bell's dialog closes, so focus lands on a visible bell.");
            await Assert.That(await page.Locator("#ts-notifications-topbar").EvaluateAsync<bool>("el => document.activeElement === el"))
                .IsTrue().Because("Closing the dialog restores focus to the bell inside the open navigation drawer.");
        }
        else
            await Assert.That(await page.Locator("details.ts-account summary").EvaluateAsync<bool>("el => document.activeElement === el"))
                .IsTrue().Because("Closing the dialog restores focus to the desktop account menu after its details panel closes.");

        var dialogUrl = page.Url;
        var reopened = await OpenDialogAsync();
        await AssertPatchAsync(reopened);
        await page.Locator("#ts-notifications-dialog[open]").WaitForAsync();
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForFunctionAsync("() => !document.querySelector('#ts-notifications-dialog')?.open");
        await page.WaitForFunctionAsync("selector => document.activeElement === document.querySelector(selector)",
            width < 768 ? "#ts-notifications-topbar" : "details.ts-account summary");
        await Assert.That(page.Url).IsEqualTo(dialogUrl).Because("Escape closes the live dialog without changing the page URL.");
        if (width < 768)
            await Assert.That(await page.Locator("#ts-notifications-topbar").EvaluateAsync<bool>("el => document.activeElement === el"))
                .IsTrue().Because("Escape restores focus to the bell inside the open navigation drawer.");
        else
            await Assert.That(await page.Locator("details.ts-account summary").EvaluateAsync<bool>("el => document.activeElement === el"))
                .IsTrue().Because("Escape restores focus to the desktop account menu after its details panel closes.");

        var afterDialog = await InboxAsync();
        await Assert.That(afterDialog.GetProperty("unread").GetInt32()).IsEqualTo(0);
        await Assert.That(afterDialog.GetProperty("total").GetInt32()).IsEqualTo(4);
        await Assert.That(afterDialog.GetProperty("notifications").EnumerateArray().All(item => item.GetProperty("status").GetString() == "Read")).IsTrue();
        using var persisted = await replica.GetAsync("/notifications?status=unread");
        await Assert.That(persisted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await persisted.Content.ReadAsStringAsync()).Contains("No unread notifications in this scope.");
        await NavigateAsync("/notifications");
        await Assert.That(await page.Locator("main .ts-notification-list article").CountAsync()).IsEqualTo(4);
        await Assert.That(await page.Locator("main .ts-notification-unread").CountAsync()).IsEqualTo(0);
        await AssertBadgeAsync(0);
        using var separateInbox = await unrelated.GetAsync("/notifications");
        await Assert.That(separateInbox.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var separateHtml = await separateInbox.Content.ReadAsStringAsync();
        await Assert.That(separateHtml).Contains("No notifications in this scope.");
        await Assert.That(separateHtml.Contains("API key expiring soon", StringComparison.Ordinal)).IsFalse()
            .Because("Native and dialog reads never expose another persona's populated inbox.");
        await Assert.That(errors.ToArray()).IsEmpty();

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200);
        }
        async Task OpenNavigationAsync()
        {
            if (await page.Locator("#ts-nav-toggle").GetAttributeAsync("aria-expanded") == "true") return;
            await page.Locator("#ts-nav-toggle").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('#ts-nav-toggle')?.getAttribute('aria-expanded') === 'true'");
        }
        async Task AssertBadgeAsync(int unread)
        {
            var label = unread == 0 ? "Notifications" : "Notifications, " + unread + " unread";
            foreach (var selector in new[] { "#ts-notifications-menu", "#ts-notifications-topbar" })
            {
                var button = page.Locator(selector);
                await Assert.That(await button.GetAttributeAsync("aria-label")).IsEqualTo(label);
                await Assert.That(await button.Locator(".ts-nav-count").CountAsync()).IsEqualTo(unread == 0 ? 0 : 1);
                if (unread > 0) await Assert.That(await button.Locator(".ts-nav-count").TextContentAsync()).IsEqualTo(unread.ToString());
            }
            if (width < 768) await Assert.That(await page.Locator("#ts-navigation #ts-notifications-topbar").CountAsync()).IsEqualTo(1)
                .Because("On mobile the bell lives inside the navigation drawer, not the topbar.");
        }
        async Task FulfillFromServerAsync(IRoute route)
        {
            var upstream = await route.FetchAsync();
            bodies[route.Request] = await upstream.TextAsync();
            await route.FulfillAsync(new() { Response = upstream });
        }
        async Task AssertPatchAsync(IResponse response)
        {
            await Assert.That(response.Status).IsEqualTo(200);
            await Assert.That((await response.HeaderValueAsync("content-type"))!).Contains("text/event-stream");
            await Assert.That(bodies[response.Request]).Contains("event: datastar-patch-elements");
            await Assert.That(response.Request.IsNavigationRequest).IsFalse();
        }
        async Task<JsonElement> InboxAsync() => await CallAsync("list_notifications", new { organizationId, projectId });
        async Task<JsonElement> CallAsync(string name, object arguments)
        {
            var rpc = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name, arguments });
            var result = rpc.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            return result.GetProperty("structuredContent");
        }
    }
}
