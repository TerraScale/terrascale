using Microsoft.Playwright;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Platform;

/// <summary>Scalar rendering across alternate colour schemes and mobile viewports.</summary>
public sealed class ScalarRenderingTests
{
    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    [Arguments(ColorScheme.Dark, 1440, 900)]
    [Arguments(ColorScheme.Light, 390, 844)]
    [Arguments(ColorScheme.Dark, 390, 844)]
    public async Task Scalar_renders_the_contract_without_browser_errors(ColorScheme colorScheme, int width, int height)
    {
        await using var server = await AuthTestServer.StartAsync();
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new()
        {
            ColorScheme = colorScheme,
            ViewportSize = new() { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => errors.Add(error);
        var external = new List<string>();
        page.Request += (_, request) =>
        {
            var url = new Uri(request.Url);
            var sameOrigin = url.Scheme == server.BaseAddress.Scheme
                && url.Host == server.BaseAddress.Host
                && url.Port == server.BaseAddress.Port;
            if (!sameOrigin && url.Scheme != "data")
            {
                external.Add(request.Url);
            }
        };

        await page.GotoAsync(new Uri(server.BaseAddress, "/scalar").ToString());
        try
        {
            await page.GetByRole(AriaRole.Heading, new() { Name = "TerraScale Auth API" }).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        }
        catch (TimeoutException)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "scalar-failure.png"), FullPage = true });
            throw new TimeoutException($"Scalar did not render. Errors: {string.Join(" | ", errors)}. Body: {await page.InnerTextAsync("body")}");
        }

        await Assert.That(errors).IsEmpty();
        await Assert.That(external).IsEmpty();
    }
}
