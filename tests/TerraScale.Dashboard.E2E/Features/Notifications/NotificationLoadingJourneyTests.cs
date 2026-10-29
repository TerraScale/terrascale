using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Notifications;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class NotificationLoadingJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task DelayedFailureCanRetryAndRefreshWithoutShowingStaleInbox(int width, int height)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        // The fixture's hosted-auth stand-in sends no email. Hold actual requests, never use timing delays.
        await BrowserAudit.SignInAsync(page, fixture.Origin);
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/profile");
        using var preference = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/language",
            new() { ["_csrf"] = csrf, ["locale"] = "pt-BR" });
        await Assert.That(preference.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await page.ReloadAsync();
        await Assert.That(await page.Locator("html").GetAttributeAsync("lang")).IsEqualTo("en");
        var requests = Channel.CreateUnbounded<(IRoute Route, TaskCompletionSource Release)>();
        await page.RouteAsync("**/notifications/dialog**", async route =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await requests.Writer.WriteAsync((route, release));
            await release.Task;
        });
        var idle = page.Locator("#ts-notifications-dialog");
        await Assert.That((await idle.Locator("p[role=status]").TextContentAsync() ?? "").Trim()).IsEmpty()
            .Because("No notifications fetch has failed yet, so the page carries no error text (QA dbs:11).");
        await Assert.That(await idle.Locator(".ts-row:has(button:text-is('Retry'))").GetAttributeAsync("hidden")).IsNotNull();
        await OpenAsync();
        var failed = await NextAsync();
        await AssertLoadingAsync();
        await failed.Route.FulfillAsync(new() { Status = 503, ContentType = "text/plain", Body = "Unavailable" });
        failed.Release.SetResult();
        var dialog = page.Locator("#ts-notifications-dialog");
        await dialog.GetByRole(AriaRole.Status).Filter(new() { HasText = "Unable to load notifications" }).WaitForAsync();
        await Assert.That(await page.Locator("#ts-notifications-body").GetAttributeAsync("aria-busy")).IsEqualTo("false");
        var inboxHref = await dialog.GetByRole(AriaRole.Link, new() { Name = "Open inbox" }).GetAttributeAsync("href");
        await Assert.That(BrowserAudit.WithoutWorkspace(new Uri(fixture.Origin, inboxHref!).ToString())).IsEqualTo(new Uri(fixture.Origin, "/notifications").ToString());
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Retry", Exact = true }).ClickAsync();
        var retry = await NextAsync();
        await AssertLoadingAsync();
        await SucceedAsync(retry);
        await page.Locator("#ts-notifications-body .ts-notifications-summary").WaitForAsync();
        await Assert.That(await page.Locator("#ts-notifications-body").GetAttributeAsync("aria-busy")).IsEqualTo("false");
        // A filter refresh must suppress the already rendered inbox as well as announce loading.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Unread", Exact = false }).ClickAsync();
        var refresh = await NextAsync();
        await AssertLoadingAsync();
        await SucceedAsync(refresh);
        await page.Locator("#ts-notifications-body .ts-notifications-summary").WaitForAsync();
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForFunctionAsync("() => !document.querySelector('#ts-notifications-dialog').open");
        var returnSelector = width < 768 ? "#ts-notifications-topbar" : "details.ts-account summary";
        await page.WaitForFunctionAsync("selector => document.activeElement === document.querySelector(selector)", returnSelector);
        if (width < 768)
            await Assert.That(await page.Locator("#ts-nav-toggle").GetAttributeAsync("aria-expanded")).IsEqualTo("true");
        await OpenAsync();
        var reopened = await NextAsync();
        await AssertLoadingAsync();
        await SucceedAsync(reopened);
        await page.Locator("#ts-notifications-body .ts-notifications-summary").WaitForAsync();
        await Assert.That(errors.ToArray()).IsEmpty();

        async Task OpenAsync()
        {
            if (width < 768)
            {
                if (await page.Locator("#ts-nav-toggle").GetAttributeAsync("aria-expanded") != "true")
                    await page.Locator("#ts-nav-toggle").ClickAsync();
                await page.Locator("#ts-notifications-topbar").ClickAsync();
            }
            else
            {
                await page.Locator("details.ts-account summary").ClickAsync();
                await page.Locator("#ts-notifications-menu").ClickAsync();
            }
        }
        async Task<(IRoute Route, TaskCompletionSource Release)> NextAsync() =>
            await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        async Task SucceedAsync((IRoute Route, TaskCompletionSource Release) request)
        {
            var upstream = await request.Route.FetchAsync();
            await Assert.That(upstream.Status).IsEqualTo(200);
            await request.Route.FulfillAsync(new() { Response = upstream });
            request.Release.SetResult();
        }
        async Task AssertLoadingAsync()
        {
            await page.Locator("#ts-notifications-dialog[open]").WaitForAsync();
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Loading notifications…" }).WaitForAsync();
            await Assert.That(await page.Locator("#ts-notifications-body").GetAttributeAsync("aria-busy")).IsEqualTo("true");
            await Assert.That(await page.Locator("#ts-notifications-body").IsVisibleAsync()).IsFalse();
            await Assert.That(await page.GetByRole(AriaRole.Button, new() { Name = "Retry", Exact = true }).IsVisibleAsync()).IsFalse();
        }
    }
}
