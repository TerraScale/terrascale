using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.DatabaseDataAccess;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class DatabaseQueryDraftBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task EmptyAndUnavailableManagementPagesStayAccessibleWithoutIssuingCredentials(int width, int height)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "oct13-management-pages");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        var mutations = new List<string>();
        page.Request += (_, request) =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (request.Method == "POST" && (path.StartsWith("/api/v1/dashboard/credentials", StringComparison.Ordinal) ||
                path.StartsWith("/api/v1/auth-management", StringComparison.Ordinal) || path.StartsWith("/api/v1/dashboard/backups", StringComparison.Ordinal)))
                mutations.Add(path);
        };
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: account.Email, password: account.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        foreach (var (path, status, message) in new[]
        {
            ("/api-keys?kind=key", 200, "No credentials match these filters."),
            ("/databases/" + id + "/backups", 200, "No restore points yet."),
            ("/subscriptions", 200, "No shared-result configurations yet."),
            ("/auth/sessions", 200, "Live end-user sessions. Revoking one signs that sign-in out."),
            ("/authentication/clients", 200, "Applications that sign users in through the authorization code flow with PKCE.")
        })
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString());
            await Assert.That(response!.Status).IsEqualTo(status);
            await Assert.That(await page.GetByText(message, new() { Exact = true }).IsVisibleAsync()).IsTrue();
            await Assert.That(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).IsTrue();
            await BrowserAudit.AssertAccessibleAsync(page, path + " at " + width + " CSS pixels");
        }
        await page.GotoAsync(new Uri(scenario.Origin, "/api-keys?kind=key").ToString());
        var open = page.GetByRole(AriaRole.Button, new() { Name = "Create key", Exact = true });
        await open.FocusAsync();
        await open.PressAsync("Enter");
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "New project key", Exact = true });
        await dialog.WaitForAsync();
        // Reopening a native dialog can cancel an earlier animation's finished promise.
        await dialog.EvaluateAsync("async el => { await Promise.allSettled(el.getAnimations({subtree:true}).map(animation => animation.finished)); }");
        await BrowserAudit.AssertAccessibleAsync(page, "The credential dialog can be inspected without issuing a key.");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assert.That(await dialog.IsVisibleAsync()).IsFalse();
        await open.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Assert.That(await dialog.IsVisibleAsync()).IsFalse();
        await open.ClickAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assert.That(await dialog.IsVisibleAsync()).IsFalse();
        await Assert.That(mutations).IsEmpty();
        await open.ClickAsync();
        await dialog.WaitForAsync();
        await dialog.EvaluateAsync("async el => { await Promise.allSettled(el.getAnimations({subtree:true}).map(animation => animation.finished)); }");
        var evidence = Path.Combine(fixture.Root, "docs/reviews/2026-10-13/database-data-access/obj/evidence");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"api-key-dialog-{width}.png"), FullPage = true });
        var accessibility = await BrowserAccessibility.RunAsync(page);
        await File.WriteAllTextAsync(Path.Combine(evidence, $"api-key-dialog-axe-{width}.json"), JsonSerializer.Serialize(
            accessibility.Violations.Select(rule => new { rule.Id, rule.Help, Nodes = rule.Nodes.Select(node => node.Target) }),
            new JsonSerializerOptions { WriteIndented = true }));
        await BrowserAudit.AssertAccessibleAsync(page, "The credential dialog can be inspected without issuing a key.");
    }

    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task NativeCheckCannotClaimSuccessForAnUnboundParameter(int width, int height)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "oct13-browser-check");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: account.Email, password: account.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        await page.GotoAsync(new Uri(scenario.Origin, "/databases/" + id + "/sql").ToString());
        await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync();
        const string statement = "SELECT * FROM users WHERE name = $1 LIMIT 20";
        await EditConsoleAsync(page, statement);
        var check = page.GetByRole(AriaRole.Button, new() { Name = "Check input", Exact = true });
        await check.FocusAsync();
        var response = await page.RunAndWaitForResponseAsync(() => check.PressAsync("Enter"), value =>
            value.Request.IsNavigationRequest && value.Request.Method == "POST" && new Uri(value.Url).AbsolutePath == "/api/v1/dashboard/data/sql/check");
        await Assert.That(response.Status).IsEqualTo((int)HttpStatusCode.UnprocessableEntity);
        await page.WaitForLoadStateAsync(LoadState.Load);
        await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync();
        await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo(statement);
        await Assert.That(await page.Locator("#sql-result").CountAsync()).IsEqualTo(0);
        var passed = await page.GetByText("Input checks passed. SQL validity has not been checked.", new() { Exact = true }).CountAsync();
        var evidence = Path.Combine(fixture.Root, "docs/reviews/2026-10-13/database-data-access/obj/evidence");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"sql-check-{width}.png"), FullPage = true });
        await Assert.That(passed).IsEqualTo(0)
            .Because("An unbound parameter is an execution error, so the visible check cannot announce success.");
        var sqlAlert = page.Locator(".ts-data-sql .ts-alert-danger[role='alert']");
        await Assert.That(await sqlAlert.CountAsync()).IsEqualTo(1);
        await Assert.That(await sqlAlert.IsVisibleAsync()).IsTrue();
        await Assert.That(await sqlAlert.InnerTextAsync()).Contains("The console cannot bind parameters");
    }

    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task SaveDialogUsesTheCurrentDraftAfterCancelCloseAndHistoryNavigation(int width, int height)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "oct13-query-draft");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: account.Email, password: account.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        var sqlPath = new Uri(scenario.Origin, "/databases/" + id + "/sql").ToString();
        var response = await page.GotoAsync(sqlPath);
        await Assert.That(response!.Status).IsEqualTo((int)HttpStatusCode.OK);
        await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync();
        const string draft = "SELECT name FROM users WHERE name = 'audit-local' LIMIT 5";
        await EditConsoleAsync(page, draft);
        var evidence = Path.Combine(fixture.Root, "docs/reviews/2026-10-13/database-data-access/obj/evidence");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"query-draft-{width}.png"), FullPage = true });
        var open = page.Locator("button[commandfor='save-query'][command='show-modal']");
        await open.FocusAsync();
        await open.PressAsync("Enter");
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Save query", Exact = true });
        await dialog.WaitForAsync();
        var actual = await page.Locator("#saved-sql").InputValueAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"save-query-{width}.png"), FullPage = true });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assert.That(await dialog.IsVisibleAsync()).IsFalse();
        await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo(draft);
        await open.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Assert.That(await dialog.IsVisibleAsync()).IsFalse();
        await open.ClickAsync();
        await dialog.GetByLabel("Name", new() { Exact = true }).FillAsync("audit-current-draft");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save query", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("savedQueryId=", StringComparison.Ordinal));
        var savedQueries = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/data/saved-queries");
        var persisted = savedQueries.EnumerateArray().Single(query => query.GetProperty("name").GetString() == "audit-current-draft").GetProperty("sql").GetString();
        await File.WriteAllTextAsync(Path.Combine(evidence, $"saved-query-values-{width}.json"), JsonSerializer.Serialize(new
        { ConsoleDraft = draft, DialogSql = actual, PersistedSql = persisted }, new JsonSerializerOptions { WriteIndented = true }));
        var savedPath = page.Url;
        await page.GotoAsync(new Uri(scenario.Origin, "/databases/" + id + "/documents").ToString());
        await page.GoBackAsync();
        await Assert.That(page.Url).IsEqualTo(savedPath);
        await page.GoForwardAsync();
        await Assert.That(page.Url).Contains("/documents");
        await Assert.That(actual).IsEqualTo(draft)
            .Because("Save query must use the current console text even before Check input or Run query.");
        await Assert.That(persisted).IsEqualTo(draft);

    }

    private static async Task EditConsoleAsync(IPage page, string statement)
    {
        var editor = page.GetByRole(AriaRole.Textbox, new() { Name = "SQL. Press Ctrl+M to toggle Tab moving focus.", Exact = true });
        await editor.FocusAsync();
        await editor.PressAsync("ControlOrMeta+A");
        await page.Keyboard.InsertTextAsync(statement);
        await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo(statement);
    }
}
