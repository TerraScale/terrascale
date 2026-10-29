using Microsoft.Playwright;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardE2EFixture>]
    [Category("Slow")]
    public sealed class SqlFormattingBrowserJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task FormatPreservesOperatorsLiteralsAndParameterTokens()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-format");
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
            var page = await context.NewPageAsync();
            await BrowserAudit.SignInAsync(page, scenario.Origin, email: account.Email, password: account.Password);
            await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
            await page.GotoAsync(new Uri(scenario.Origin, "/databases/" + id + "/sql").ToString());
            await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync();
            foreach (var expression in new[] { "'a' || 'b'", "8 >> 1", "1 << 2", "2 == 2", "2 != 1", "2 <> 1", "2 >= 1", "1 <= 2", "'{\"a\":1}' ->> '$.a'", "'{\"a\":1}' -> '$.a'", "X'ABCD'", ".5 + 0xFF", ":name, @other, ?1, $value", "1 - -2" })
            {
                var sql = "SELECT " + expression + " FROM items WHERE id = 1";
                await page.Locator("#sql").EvaluateAsync("(el, sql) => { el.value = sql; el.dispatchEvent(new Event('input', {bubbles:true})); }", sql);
                await page.GetByRole(AriaRole.Button, new() { Name = "Format", Exact = true }).ClickAsync();
                await page.WaitForFunctionAsync("expression => document.querySelector('#sql').value.includes(expression + '\\nFROM')", expression);
                await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo("SELECT " + expression + "\nFROM items\nWHERE id = 1");
            }
        }
    }
}
