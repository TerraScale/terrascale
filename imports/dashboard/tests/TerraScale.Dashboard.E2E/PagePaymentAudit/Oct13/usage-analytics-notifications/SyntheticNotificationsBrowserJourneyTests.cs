using System.Collections.Concurrent;
using System.Net;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class SyntheticNotificationsBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task UnreadPaginationAndDialogKeyboardFocusUseOnlyLocalSyntheticAccounts(int width, int height)
    {
        await using var host = await PageAuditHost.StartAsync(fixture.Root);
        using var client = host.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client, "/notifications");
        using var seeded = await HttpJourney.PostFormAsync(client, "/__test/oct13/page-audit/seed", new() { ["_csrf"] = csrf });
        await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        var outsideOrigins = new ConcurrentQueue<string>();
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.GetLeftPart(UriPartial.Authority) == host.Origin.GetLeftPart(UriPartial.Authority)) await route.ContinueAsync();
            else { outsideOrigins.Enqueue(uri.GetLeftPart(UriPartial.Authority)); await route.AbortAsync(); }
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        await BrowserAudit.SignInAsync(page, host.Origin);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == host.Origin.ToString());
        await NavigateAsync("/notifications?status=unread&page=2");
        await Assert.That(await page.Locator("main .ts-notification-unread").CountAsync()).IsEqualTo(2);
        await BrowserAudit.AssertAccessibleAsync(page, "Synthetic notification page 2 at " + width);
        var first = page.Locator("main .ts-notification-unread form").First;
        var action = (await first.GetAttributeAsync("action"))!;
        var marked = await page.RunAndWaitForResponseAsync(
            () => first.GetByRole(AriaRole.Button, new() { Name = "Mark read", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == action.Split('?')[0]);
        await Assert.That(marked.Status).IsEqualTo(303);
        await page.WaitForFunctionAsync("() => document.querySelectorAll('main .ts-notification-unread').length === 1");
        await Assert.That(new Uri(BrowserAudit.WithoutWorkspace(page.Url)).PathAndQuery).IsEqualTo("/notifications?page=2&status=unread");
        await page.GetByRole(AriaRole.Button, new() { Name = "Mark all read", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == new Uri(host.Origin, "/notifications?status=unread").ToString());
        await Assert.That(await page.GetByText("No unread notifications in this scope.", new() { Exact = true }).IsVisibleAsync()).IsTrue();
        await Assert.That(await page.GetByRole(AriaRole.Button, new() { Name = "Mark all read", Exact = true }).IsDisabledAsync()).IsTrue();

        var returnFocus = width < 768 ? "#ts-notifications-topbar" : "details.ts-account summary";
        var unchangedUrl = page.Url;
        var output = BrowserAudit.Output(fixture.Root, "oct13-synthetic-notifications");
        foreach (var closeByEscape in new[] { false, true })
        {
            if (width < 768 && await page.Locator("#ts-nav-toggle").GetAttributeAsync("aria-expanded") != "true")
            {
                await page.Locator("#ts-nav-toggle").ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('#ts-nav-toggle')?.getAttribute('aria-expanded') === 'true'");
            }
            if (width >= 768) await page.Locator("details.ts-account summary").ClickAsync();
            var opener = page.Locator(width < 768 ? "#ts-notifications-topbar" : "#ts-notifications-menu");
            await opener.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            var dialog = page.Locator("#ts-notifications-dialog[open]");
            await dialog.WaitForAsync();
            await dialog.Locator(".ts-notification-list article").Nth(9).WaitForAsync();
            await Assert.That(await dialog.Locator(".ts-notification-list article").CountAsync()).IsEqualTo(10);
            await Assert.That(await dialog.InnerTextAsync()).DoesNotContain("Hidden project notice");
            await Assert.That(await dialog.InnerTextAsync()).DoesNotContain("Hidden organization notice");
            var presentation = await page.EvaluateAsync<string>("() => JSON.stringify({animations: document.getAnimations().map(a => ({state: a.playState, target: a.effect?.target?.className})), dialog: getComputedStyle(document.querySelector('#ts-notifications-dialog')).opacity, meta: getComputedStyle(document.querySelector('#ts-notifications-body .ts-meta')).color})");
            await File.WriteAllTextAsync(Path.Combine(output, "dialog-presentation-" + width + "-" + closeByEscape + ".json"), presentation);
            // Patched content can exist before the dialog's opacity animation has finished.
            await dialog.EvaluateAsync("async el => { await document.fonts.ready; await Promise.all(el.getAnimations({subtree: true}).map(animation => animation.finished)); }");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "notifications-dialog-" + width + "-" + closeByEscape + ".png"), FullPage = true });
            await BrowserAudit.AssertAccessibleAsync(page, "Synthetic notification dialog at " + width);
            if (closeByEscape) await page.Keyboard.PressAsync("Escape");
            else await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await page.WaitForFunctionAsync("selector => !document.querySelector('#ts-notifications-dialog').open && document.activeElement === document.querySelector(selector)", returnFocus);
            await Assert.That(page.Url).IsEqualTo(unchangedUrl);
        }
        await Assert.That(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 2")).IsTrue();
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "notifications-empty-" + width + ".png"), FullPage = true });
        await Assert.That(errors.ToArray()).IsEmpty();
        await Assert.That(outsideOrigins.ToArray()).IsEmpty().Because("Only the isolated local test host is permitted.");

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(host.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200);
        }
    }
}
