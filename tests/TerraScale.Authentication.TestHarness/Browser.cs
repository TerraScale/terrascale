using Microsoft.Playwright;

namespace TerraScale.Authentication.Tests;

/// <summary>Launches headless Chromium for browser-level checks.</summary>
public static class Browser
{
    /// <summary>
    /// Starts Chromium. <c>TS_AUTH_CHROMIUM_PATH</c> selects a system browser (CI images and
    /// NixOS); otherwise Playwright's own download is used.
    /// </summary>
    public static async Task<(IPlaywright Playwright, IBrowser Browser)> LaunchAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("TS_AUTH_CHROMIUM_PATH");
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = string.IsNullOrWhiteSpace(executable) ? null : executable,
            });
            return (playwright, browser);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }
}

/// <summary>
/// One headless Chromium per test session, launched by the first browser test. Each test opens its own
/// context, so cookies and storage stay isolated while the launch cost is paid once.
/// </summary>
public sealed class BrowserFixture : IAsyncDisposable
{
    private readonly Lazy<Task<(IPlaywright Playwright, IBrowser Browser)>> launch = new(Browser.LaunchAsync);

    public async Task<IBrowser> BrowserAsync() => (await this.launch.Value).Browser;

    public async ValueTask DisposeAsync()
    {
        if (this.launch.IsValueCreated && this.launch.Value.IsCompletedSuccessfully)
        {
            var (playwright, browser) = this.launch.Value.Result;
            await browser.DisposeAsync();
            playwright.Dispose();
        }
    }
}
