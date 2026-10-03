using Microsoft.Playwright;

namespace TerraScale.Auth.Tests;

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
