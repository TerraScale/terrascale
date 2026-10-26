using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    [Test, Category("Slow")]
    public async Task ProjectRenameNavigatesWithDatastarActiveAndPreservesRejectedDraft()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("native-project-rename");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var loginPage = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
        await BrowserAudit.SignInAsync(loginPage, platform.Origin, "/project", user.Email, user.Password);
        var destination = loginPage.Url;
        var output = BrowserAudit.Output(Directory.GetCurrentDirectory(), "q03-project-settings");
        await using var recording = await browser.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true,
            StorageState = await loginPage.Context.StorageStateAsync(),
            ViewportSize = new() { Width = 1440, Height = 1000 },
            ColorScheme = ColorScheme.Dark,
            RecordVideoDir = output
        });
        // Record only the authenticated flow; hide account identity before the first rendered frame.
        await recording.RouteAsync("**/css/terrascale.css*", async route =>
        {
            var response = await route.FetchAsync();
            // Extend the same-origin stylesheet so the production CSP remains intact.
            await route.FulfillAsync(new() { Response = response, Body = await response.TextAsync() + "\n.ts-account-trigger,.ts-account-card{visibility:hidden!important}" });
        });
        await loginPage.Context.CloseAsync();
        var page = await recording.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(destination);
        await WaitForProjectNativeFormAsync(page);
        await Assert.That(await page.Locator(".ts-account-trigger").EvaluateAsync<string>("el => getComputedStyle(el).visibility")).IsEqualTo("hidden");
        await ShotAsync("rename-ready");
        await page.GetByLabel("Project name", new() { Exact = true }).FillAsync("!!!");
        await SubmitProjectNativeFormAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Rename project", Exact = true }),
            "/api/v1/dashboard/project/rename", 422);
        await WaitForProjectNativeFormAsync(page);
        await Assert.That(await page.GetByLabel("Project name", new() { Exact = true }).InputValueAsync()).IsEqualTo("!!!");
        await Assert.That(await page.GetByRole(AriaRole.Alert).InnerTextAsync()).Contains("characters including a letter or digit");
        await ShotAsync("rename-invalid-name");
        await page.GetByLabel("Project name", new() { Exact = true }).FillAsync("Native project rename");
        await SubmitProjectNativeFormAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Rename project", Exact = true }),
            "/api/v1/dashboard/project/rename", 303);
        await page.GetByText("Project renamed.", new() { Exact = true }).WaitForAsync();
        await Assert.That(await page.Locator("main dd").First.InnerTextAsync()).IsEqualTo("Native project rename");
        await ShotAsync("rename-success");
        await Assert.That(errors).IsEmpty();
        await Assert.That(platform.Producer.Failures).IsEmpty();
        await page.CloseAsync();
        await recording.CloseAsync();
        await page.Video!.SaveAsAsync(Path.Combine(output, "rename-flow.webm"));

        Task ShotAsync(string name) => page.ScreenshotAsync(new()
        { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide });
    }

    private static async Task WaitForProjectNativeFormAsync(IPage page)
    {
        // This binding is produced by Datastar; waiting for it prevents a false pass
        // from submitting before the cancelling event plugin has initialized.
        await page.WaitForFunctionAsync("() => document.querySelector('main form[data-submit-once]')?.getAttribute('aria-busy') === 'false'");
    }

    private static async Task SubmitProjectNativeFormAsync(IPage page, ILocator button, string path, int status)
    {
        bool? busy = null;
        EventHandler<IConsoleMessage> observe = (_, message) =>
        {
            if (message.Text.StartsWith("q03-native-submit:", StringComparison.Ordinal)) busy = message.Text == "q03-native-submit:true";
        };
        page.Console += observe;
        try
        {
            // Observe after the app's submit listeners, without a locator read
            // that waits for a paused native navigation to finish.
            await page.EvaluateAsync("""
                () => window.addEventListener('submit', event => console.debug('q03-native-submit:' +
                    (event.submitter?.getAttribute('aria-busy') === 'true' && event.target.getAttribute('aria-busy') === 'true')), {once: true})
                """);
            var response = await page.RunAndWaitForResponseAsync(() => button.ClickAsync(),
                response => response.Request.IsNavigationRequest && response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == path);
            await Assert.That(response.Status).IsEqualTo(status);
            await Assert.That(busy).IsTrue();
            await page.WaitForLoadStateAsync();
        }
        finally { page.Console -= observe; }
    }
}
