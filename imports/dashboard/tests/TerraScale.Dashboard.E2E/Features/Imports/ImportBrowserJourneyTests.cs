using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Imports;

// The browser fingerprints the export with the dashboard's own SHA-256 and uploads it in 4 MiB parts;
// the test-only TerraBase producer refuses any byte or digest that differs, so a pass proves both.
[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class ImportBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task GuidedPostgresImportUploadsReviewsConfirmsAndVerifiesInTheBrowser()
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(page.Url + " :: " + error);
        var output = BrowserAudit.Output(fixture.Root, "imports");

        await BrowserAudit.SignInAsync(page, scenario.Origin);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        await page.GotoAsync(new Uri(scenario.Origin, "/databases").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Import", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => Regex.IsMatch(BrowserAudit.WithoutWorkspace(url), "/imports$"));
        await BrowserAudit.AssertAccessibleAsync(page, "The import start page meets WCAG 2.2 AA.");
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "1-start.png"), FullPage = true });

        // Two parts: 4 MiB, then the rest.
        var dump = new byte[4 * 1024 * 1024 + 4321];
        Encoding.UTF8.GetBytes("-- TerraScale import fixture: shop\n").CopyTo(dump, 0);
        Random.Shared.NextBytes(dump.AsSpan(64));
        await page.Locator("#import-file").SetInputFilesAsync(new FilePayload { Name = "dump.sql", MimeType = "application/sql", Buffer = dump });
        await page.GetByRole(AriaRole.Button, new() { Name = "Upload and preview" }).ClickAsync();
        await page.WaitForURLAsync(url => Regex.IsMatch(BrowserAudit.WithoutWorkspace(url), "/imports/import-[0-9a-z]{26}$"));
        await page.GetByText("1 blocking issue", new() { Exact = true }).WaitForAsync();
        await Assert.That(await page.Locator("main").InnerTextAsync()).Contains("text[] has no mapping; exclude the column");
        await Assert.That(await page.Locator("form:is([action$='/start'],[action*='/start?'])").CountAsync()).IsEqualTo(0);
        await BrowserAudit.AssertAccessibleAsync(page, "A blocked preview meets WCAG 2.2 AA.");
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "2-blocked-preview.png"), FullPage = true });

        await page.GetByLabel("Exclude public.orders.tags").CheckAsync(new() { Force = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Update preview" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Confirm this preview" }).WaitForAsync();
        var main = await page.Locator("main").InnerTextAsync();
        foreach (var disclosure in new[] { "Stored as a different type", "Constraints not enforced", "public.orders.tags is not imported.", "generated ULID", "Do not write to" })
            await Assert.That(main).Contains(disclosure);
        await BrowserAudit.AssertAccessibleAsync(page, "The confirmation step meets WCAG 2.2 AA.");
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "3-confirm-preview.png"), FullPage = true });

        await page.GetByLabel("I reviewed this exact preview and accept every difference listed.").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Start import" }).ClickAsync();
        await page.Locator("progress[aria-label='Rows written']").WaitForAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "4-copying.png"), FullPage = true });
        await page.GetByText("Import verified").WaitForAsync(new() { Timeout = 60000 });
        await Assert.That(await page.Locator("main").InnerTextAsync()).Contains("Every row count and checksum matches the export.");
        await BrowserAudit.AssertAccessibleAsync(page, "The verified import meets WCAG 2.2 AA.");
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "5-verified.png"), FullPage = true });
        await Assert.That(errors).IsEmpty();
    }
}
