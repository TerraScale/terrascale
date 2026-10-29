using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class AuthenticationResponsiveBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(375)]
    [Arguments(768)]
    [Arguments(1280)]
    [Arguments(1920)]
    public async Task ProviderActionsAndConfigurationFitTheViewportBeforeAndAfterSaving(int width)
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = 1000 } });
        var page = await context.NewPageAsync();
        await BrowserAudit.SignInAsync(page, scenario.Origin, returnTo: "/auth/social");
        await page.GotoAsync(new Uri(scenario.Origin, "/auth/social").ToString());
        await Fits();
        var google = page.Locator("#auth-provider-google");
        await google.GetByRole(AriaRole.Button, new() { Name = "Configure", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#auth-provider-google-dialog")).ToBeVisibleAsync();
        await Fits();
        await page.Locator("#auth-provider-google-client-id").FillAsync(new string('a', 240) + ".example");
        await page.Locator("#auth-provider-google-secret").FillAsync("synthetic-responsive-test-secret");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save Google", Exact = true }).ClickAsync();
        await Expect(google).ToContainTextAsync("Configured");
        await Expect(google.GetByRole(AriaRole.Button, new() { Name = "Update", Exact = true })).ToBeVisibleAsync();
        await Expect(google.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true })).ToBeVisibleAsync();
        await Fits();
        await page.GotoAsync(new Uri(scenario.Origin, "/auth/sign-in").ToString());
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save sign-in methods", Exact = true })).ToBeVisibleAsync();
        await Fits();
        await page.GotoAsync(new Uri(scenario.Origin, "/auth/configuration").ToString());
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true })).ToBeVisibleAsync();
        await Fits();

        async Task Fits()
        {
            await page.EvaluateAsync("() => document.fonts.ready");
            await Assert.That(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth")).IsTrue();
            foreach (var button in await page.Locator(".ts-idp-content button:visible").AllAsync())
            {
                var bounds = await button.BoundingBoxAsync();
                await Assert.That(bounds is not null && bounds.X >= 0 && bounds.X + bounds.Width <= width).IsTrue();
            }
        }
    }
}
