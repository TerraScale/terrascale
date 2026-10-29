using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.AccountsMembersSettings;

[ClassDataSource<AccountsAuditScenario>]
[Category("Slow")]
public sealed class AccountsMembersSettingsBrowserAuditJourneyTests(AccountsAuditScenario scenario)
{
    [Test]
    [Arguments(1366)]
    [Arguments(320)]
    public async Task ProfileValidationKeepsTheVisibleDraft(int width)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = 900 } });
        await LoginAsync(page, "/profile");
        var name = page.GetByLabel("Display name");
        var phone = page.GetByLabel("Phone number (optional)");
        await name.FillAsync("Browser draft name");
        await phone.FillAsync(new string('9', 65));
        await ShotAsync(page, "profile-draft-" + width);
        var response = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Save profile", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && BrowserAudit.WithoutWorkspace(response.Url).EndsWith("/api/v1/dashboard/profile", StringComparison.Ordinal));
        await Assert.That(response.Status).IsEqualTo(422);
        await page.GetByRole(AriaRole.Alert).WaitForAsync();
        await ShotAsync(page, "profile-error-" + width);
        await BrowserAudit.AssertAccessibleAsync(page, "The invalid profile remains readable by keyboard and assistive technology at " + width + "px.");
        await Assert.That(await name.InputValueAsync()).IsEqualTo("Browser draft name");
        await Assert.That(await phone.InputValueAsync()).IsEqualTo(new string('9', 65));
    }

    [Test]
    [Arguments(1366)]
    [Arguments(320)]
    public async Task InvitationRoleOptionsIncludeTheSupportedViewer(int width)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = 900 } });
        await LoginAsync(page, "/organization?tab=members");
        var role = page.GetByLabel("Role", new() { Exact = true });
        await role.ScrollIntoViewIfNeededAsync();
        await ShotAsync(page, "invitation-options-" + width);
        var options = await role.Locator("option").EvaluateAllAsync<string[]>("options => options.map(option => option.value)");
        await Assert.That(options.Contains("viewer", StringComparer.Ordinal)).IsTrue()
            .Because("The native form must offer the read-only role supported by the current ts-auth contract.");
        await Assert.That(options.Contains("admin", StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments(1366)]
    [Arguments(320)]
    public async Task CancellationKeyboardScrollingAndEmptyFiltersRemainAccessible(int width)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = 900 } });
        await LoginAsync(page, "/organization?tab=members");
        var remove = page.Locator("#team-members tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Remove access", Exact = true });
        var dialog = page.Locator("dialog[open]");
        await remove.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await dialog.WaitForAsync();
        await BrowserAudit.AssertAccessibleAsync(page, "The removal dialog has a name and traps keyboard focus.");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(remove).ToBeFocusedAsync();
        foreach (var close in new[] { "Cancel", "Close" })
        {
            await remove.ClickAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = close, Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(remove).ToBeFocusedAsync();
        }
        await Assert.That(await page.Locator("#team-members tbody tr").CountAsync()).IsEqualTo(10);
        await Assert.That(await page.EvaluateAsync<int>("document.documentElement.scrollWidth")).IsLessThanOrEqualTo(width);
        var table = page.GetByRole(AriaRole.Region, new() { Name = "Organization members table", Exact = true });
        await table.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        if (width == 320)
            await Assert.That(await table.EvaluateAsync<double>("table => table.scrollLeft")).IsGreaterThan(0);
        await page.Locator("#member-search").FillAsync("not-a-real-member");
        await page.Keyboard.PressAsync("Enter");
        await page.GetByText("No members match", new() { Exact = true }).WaitForAsync();
        await BrowserAudit.AssertAccessibleAsync(page, "The empty member filter has a visible recovery link at " + width + "px.");
        await ShotAsync(page, "members-empty-" + width);
        await page.GetByRole(AriaRole.Link, new() { Name = "Clear filters", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#team-members tbody tr")).ToHaveCountAsync(10);
        await page.GotoAsync(new Uri(scenario.Origin, "/profile").ToString());
        await page.GoBackAsync();
        await Assertions.Expect(page.Locator("#team-members tbody tr")).ToHaveCountAsync(10);
        await page.GoForwardAsync();
        await page.GetByLabel("Display name").WaitForAsync();
    }

    private async Task LoginAsync(IPage page, string path)
    {
        await BrowserAudit.SignInAsync(page, scenario.Origin, path, "owner@accounts-audit.example.test", AccountsAuditScenario.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == new Uri(scenario.Origin, path).ToString());
    }

    private Task<byte[]> ShotAsync(IPage page, string name)
    {
        var directory = Path.Combine(scenario.Root, "docs/reviews/2026-10-13/accounts-members-settings/evidence");
        Directory.CreateDirectory(directory);
        return page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png") });
    }
}
