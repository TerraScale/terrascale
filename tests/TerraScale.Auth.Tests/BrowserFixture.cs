namespace TerraScale.Auth.Tests;

using Microsoft.Playwright;

/// <summary>
/// Shared headless Chromium for the hosted-UI E2E tests. One browser per test
/// process; every test gets its own context (cookies, storage, user agent) via
/// <see cref="NewPageAsync(Microsoft.Playwright.BrowserNewPageOptions?)"/>.
/// Requires the Playwright browsers (rev matching the Microsoft.Playwright
/// package) under ~/.cache/ms-playwright — see the CI install step.
/// </summary>
public static class BrowserFixture
{
    private static readonly Lazy<Task<(IPlaywright Playwright, IBrowser Browser)>> Shared = new(StartAsync);

    private static async Task<(IPlaywright Playwright, IBrowser Browser)> StartAsync()
    {
        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        return (playwright, browser);
    }

    /// <summary>A fresh page whose disposal also closes its isolated context.</summary>
    public static async Task<IPage> NewPageAsync(BrowserNewPageOptions? options = null)
    {
        var (_, browser) = await Shared.Value;
        return await browser.NewPageAsync(options);
    }
}
