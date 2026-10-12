using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Category("Slow")]
public sealed class DashboardBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public Task DatabaseProfileBillingAndDatastarDesktopAndMobile() => BrowserJourneys.RunAsync(fixture, "dashboard-flow.mjs");

    [Test]
    public async Task AccessibleAuthPagesAndNativePasswordControls()
    {
        var output = BrowserAudit.Output(fixture.Root, "");
        var origin = fixture.Origin.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        foreach (var (name, width, height) in new[] { ("desktop", 1440, 1000), ("mobile", 390, 844) })
        {
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
            var page = await context.NewPageAsync();
            var errors = new ConcurrentQueue<string>();
            page.PageError += (_, error) => errors.Enqueue(error);
            var response = await page.GotoAsync(origin + "/login");
            await Assert.That(response!.Status).IsEqualTo(200);
            var password = page.Locator("#password");
            await password.FillAsync("browser-test-password");
            await page.GetByRole(AriaRole.Button, new() { Name = "Show password" }).ClickAsync();
            await Assert.That(await password.GetAttributeAsync("type")).IsEqualTo("text");
            await page.GetByRole(AriaRole.Button, new() { Name = "Hide password" }).ClickAsync();
            await Assert.That(await password.GetAttributeAsync("type")).IsEqualTo("password");
            await password.FillAsync("");
            await page.EvaluateAsync("() => document.fonts.ready");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"login-{name}.png"), FullPage = true });
            await BrowserAudit.AssertAccessibleAsync(page, "WCAG 2.2 AA on /login " + name);
            await page.GetByRole(AriaRole.Link, new() { Name = "Forgot password" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Reset password" }).WaitForAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"reset-{name}.png"), FullPage = true });
            await page.GotoAsync(origin + "/login/code");
            await page.GetByRole(AriaRole.Heading, new() { Name = "Sign in with a code" }).WaitForAsync();
            await page.GotoAsync(origin + "/missing");
            await page.GetByRole(AriaRole.Heading, new() { Name = "We can’t find that page." }).WaitForAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"notfound-{name}.png"), FullPage = true });
            await Assert.That(errors).IsEmpty().Because("No browser script errors");
        }
    }
}
