using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class DesignSystemBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task SignedInAuditBillingAndKeyboardChartUseSharedPrimitives()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await RecordingAsync(browser, scenario.Origin, scenario.Personas.Seeded.Email, scenario.Personas.Seeded.Password);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15000);

        await NavigateAsync(page, scenario.Origin, "/organization?tab=audit");
        var menu = page.Locator("#recent-activity details.ts-menu");
        var trigger = menu.Locator("summary[aria-label='Export audit events']");
        await trigger.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await menu.Locator(".ts-menu-panel").WaitForAsync();
        await Assert.That(await menu.GetAttributeAsync("open")).IsNotNull();
        await Assert.That(await menu.Locator(".ts-menu-panel").IsVisibleAsync()).IsTrue();
        await Assert.That(await menu.Locator("a.ts-menu-item").CountAsync()).IsEqualTo(3);
        await CaptureAsync(page, "audit-export-open");
        await page.Keyboard.PressAsync("Escape");
        await Assert.That(await menu.GetAttributeAsync("open")).IsNull();

        await NavigateAsync(page, scenario.Origin, "/organization?tab=billing");
        await Assert.That(await page.Locator("header.ts-row.ts-row-between").InnerTextAsync()).IsEqualTo("Plans and billing");
        await Assert.That(await page.Locator("#billing-summary.ts-card > .ts-card-head .ts-card-title").InnerTextAsync()).IsEqualTo("Current plan");
        await Assert.That(await page.Locator("#billing-summary > .ts-card-body").IsVisibleAsync()).IsTrue();
        await CaptureAsync(page, "billing-summary");

        await NavigateAsync(page, scenario.Origin, "/");
        var chart = page.Locator(".ts-chart-shell").Filter(new() { Has = page.Locator(".ts-chart-column") }).First;
        await chart.FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        var point = chart.Locator(".ts-chart-column:focus-visible");
        await point.WaitForAsync();
        await Assert.That(await point.EvaluateAsync<string>("el => getComputedStyle(el).outlineStyle")).IsEqualTo("solid");
        await Assert.That(await point.EvaluateAsync<bool>("el => parseFloat(getComputedStyle(el).outlineWidth) >= 2")).IsTrue();
        await Assert.That(await point.EvaluateAsync<bool>("""
            el => {
                const probe = document.createElement('span');
                probe.style.color = 'var(--ts-focus)';
                el.closest('.ts-chart-shell').append(probe);
                const matches = getComputedStyle(el).outlineColor === getComputedStyle(probe).color;
                probe.remove();
                return matches;
            }
            """)).IsTrue().Because("Keyboard chart focus uses the shared focus token.");
        await CaptureAsync(page, "chart-keyboard-focus");
    }

    [Test]
    public async Task SignedInEmptyStorageUsesSharedCardHeader()
    {
        var populated = false;
        const string bucketId = "01JA00000000000000000000B1";
        var rules = new TerraBaseFileRules("/userId", new(true, false, []), new(true, false, []));
        var bucket = new TerraBaseFileBucket(bucketId, "avatars", "gru", rules);
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            Task.FromResult<IResult?>(http.Request.Method != "GET" || !http.Request.Path.Value!.Contains("/buckets", StringComparison.Ordinal) ? null
                : http.Request.Path.Value.EndsWith("/buckets", StringComparison.Ordinal)
                    ? Results.Json(new { items = populated ? new[] { bucket } : [], next = (string?)null })
                    : Results.Json(new { items = Array.Empty<object>(), next = (string?)null })));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await RecordingAsync(browser, scenario.Origin);
        var page = await context.NewPageAsync();
        await NavigateAsync(page, scenario.Origin, "/storage");
        var buckets = page.Locator("section.ts-card[aria-label='File buckets']");
        await Assert.That(await buckets.Locator(".ts-card-head > .ts-card-title").InnerTextAsync()).IsEqualTo("Buckets");
        await Assert.That(await buckets.Locator(".ts-empty-title").InnerTextAsync()).IsEqualTo("No buckets yet");
        await CaptureAsync(page, "storage-empty");
        var create = page.Locator("details.ts-card").Filter(new() { HasText = "Create bucket" });
        await create.Locator("summary.ts-card-head").ClickAsync();
        await Assert.That(await create.GetAttributeAsync("open")).IsNotNull();
        await CaptureAsync(page, "storage-create-expanded");
        populated = true;
        await NavigateAsync(page, scenario.Origin, "/storage?bucket=" + bucketId);
        var access = page.Locator("details.ts-card").Filter(new() { HasText = "Bucket access rules" });
        await access.Locator("summary.ts-card-head").ClickAsync();
        await Assert.That(await access.GetAttributeAsync("open")).IsNotNull();
        await Assert.That(await access.GetByRole(AriaRole.Button, new() { Name = "Save rules" }).IsVisibleAsync()).IsTrue();
        await CaptureAsync(page, "storage-rules-expanded");
    }

    private async Task<IBrowserContext> RecordingAsync(IBrowser browser, Uri origin, string email = "user@example.com", string password = "test-password")
    {
        await using var login = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await BrowserAudit.SignInAsync(await login.NewPageAsync(), origin, email: email, password: password);
        var recording = await browser.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true,
            StorageState = await login.StorageStateAsync(),
            ViewportSize = new() { Width = 1440, Height = 1000 },
            RecordVideoDir = BrowserAudit.Output(fixture.Root, "design-system"),
            RecordVideoSize = new() { Width = 1440, Height = 1000 }
        });
        // Redact before first paint so both screenshots and videos hide synthetic identity data.
        await recording.RouteAsync("**/css/terrascale.css*", async route =>
        {
            var response = await route.FetchAsync();
            await route.FulfillAsync(new()
            {
                Response = response,
                Body = await response.TextAsync() + "\n.ts-account-trigger,.ts-account-card,.ts-context-text,.ts-mobile-context,#billing-summary .ts-card-description,.ts-billing-facts > div:last-child,.ts-audit-table td:nth-child(2),.ts-audit-table td:nth-child(4),.ts-audit-table td:nth-child(6){visibility:hidden!important}"
            });
        });
        return recording;
    }

    private static async Task NavigateAsync(IPage page, Uri origin, string path)
    {
        var response = await page.GotoAsync(new Uri(origin, path).ToString());
        await Assert.That(response!.Status).IsEqualTo(200).Because(path);
        await page.EvaluateAsync("() => document.fonts.ready");
        await Assert.That(await page.Locator(".ts-account-trigger").EvaluateAsync<string>("el => getComputedStyle(el).visibility")).IsEqualTo("hidden");
    }

    private Task CaptureAsync(IPage page, string flow) => page.ScreenshotAsync(new()
    {
        Path = Path.Combine(BrowserAudit.Output(fixture.Root, "design-system"), flow + ".png"),
        FullPage = true,
        Caret = ScreenshotCaret.Hide
    });
}
