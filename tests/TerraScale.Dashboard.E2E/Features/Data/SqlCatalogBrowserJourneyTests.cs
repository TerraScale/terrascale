using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class SqlCatalogBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task DeclaredNamesCompleteAndRefreshKeepsSqlWhileAnnouncingLoading()
    {
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) return null;
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            var sql = body.RootElement.GetProperty("statements")[0].GetProperty("sql").GetString()!;
            if (sql.StartsWith("SELECT name FROM sqlite_master", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref requests) > 1) { loading.TrySetResult(); await release.Task.WaitAsync(http.RequestAborted); }
                return Results.Json(new { results = new[] { SqlCatalogJourneyTests.Result(["name"], [["empty_table"]]) } });
            }
            return Results.Json(new { results = new[] { SqlCatalogJourneyTests.Result(["cid", "name", "type", "notnull", "dflt_value", "pk"], [[0, "declared_only", "TEXT", 0, null, 0]]) } });
        });
        using var client = scenario.CreateClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-complete");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: account.Email, password: account.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        await page.GotoAsync(new Uri(scenario.Origin, "/databases/" + id + "/sql").ToString());
        await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync();
        await page.Locator("#sql-editor .monaco-editor").ClickAsync();
        await page.Keyboard.TypeAsync("SELECT * FROM emp");
        await page.Keyboard.PressAsync("Control+Space");
        await page.GetByRole(AriaRole.Option).Filter(new() { HasText = "empty_table" }).WaitForAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo("SELECT * FROM \"empty_table\"");
        await page.Keyboard.PressAsync("Control+Home");
        for (var index = 0; index < 7; index++) await page.Keyboard.PressAsync("ArrowRight");
        await page.Keyboard.PressAsync("Delete");
        await page.Keyboard.TypeAsync("dec");
        await page.Keyboard.PressAsync("Control+Space");
        await page.GetByRole(AriaRole.Option).Filter(new() { HasText = "declared_only" }).WaitForAsync();
        await page.Keyboard.PressAsync("Enter");
        var sql = await page.Locator("#sql").InputValueAsync();
        await Assert.That(sql).Contains("\"declared_only\"");
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Refresh SQL tables", Exact = true }).ClickAsync();
        try
        {
            await loading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(await page.Locator("#sql-tables").GetAttributeAsync("aria-busy")).IsEqualTo("true");
        }
        finally { release.TrySetResult(); }
        await refresh;
        await page.WaitForFunctionAsync("() => document.querySelector('#sql-tables')?.getAttribute('aria-busy') === 'false'");
        await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo(sql);
        await Assert.That(Volatile.Read(ref requests)).IsEqualTo(2);
    }
}
