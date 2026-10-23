using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class DataBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task CollectionSelectionSearchAndSqlAdviceUseTheNativePages(int width, int height)
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        var bootFailures = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) =>
        {
            if (message.Text.StartsWith("Code editor unavailable:", StringComparison.Ordinal) ||
                message.Text.StartsWith("SQL completion unavailable:", StringComparison.Ordinal)) bootFailures.Enqueue(message.Text);
        };
        page.RequestFailed += (_, request) =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (path.StartsWith("/js/", StringComparison.Ordinal)) bootFailures.Enqueue(path + ": " + request.Failure);
        };
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: scenario.Personas.Seeded.Email, password: scenario.Personas.Seeded.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        const string database = "/databases/0Z793APJ2ECFW2F7P6J9F2RKVY";
        await NavigateAsync(database + "/documents");
        await Assert.That(await page.TitleAsync()).IsEqualTo("core-primary · Documents · TerraScale");
        await Assert.That(Regex.Replace(await page.Locator(".ts-data-nav-link .ts-meta.ts-mono").InnerTextAsync(), @"\s+", " ").Trim()).IsEqualTo("3 documents");
        await Assert.That(await page.Locator("#collection-select option:checked").InnerTextAsync()).IsEqualTo("orders (3)");
        await Assert.That(await page.Locator("form[aria-label='Search documents'] button").CountAsync()).IsEqualTo(0);
        await Assert.That(await page.Locator("#documents tbody tr").CountAsync()).IsEqualTo(3);
        await Assert.That(await page.Locator("#documents th[aria-sort='ascending'] .ts-data-sort svg path").GetAttributeAsync("d")).IsEqualTo("m18 15-6-6-6 6");
        await Assert.That(await page.Locator("#documents th .ts-data-sort svg").CountAsync()).IsEqualTo(1);
        if (width < 600)
        {
            await Assert.That(await page.Locator("#collection-select").IsVisibleAsync()).IsTrue();
            await page.Locator("#collection-select").SelectOptionAsync("orders");
            await page.Locator(".ts-data-nav-select").GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url).EndsWith("/documents?collection=orders", StringComparison.Ordinal));
        }
        await page.Locator("input[name=q]").FillAsync("cus_1002");
        await page.Locator("input[name=q]").PressAsync("Enter");
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url).EndsWith("/documents/orders?q=cus_1002", StringComparison.Ordinal));
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assert.That(await page.Locator("#documents tbody tr").CountAsync()).IsEqualTo(1);
        await Assert.That(await page.Locator("#documents tbody").InnerTextAsync()).Contains("980.0");
        await page.GetByRole(AriaRole.Link, new() { Name = "Clear", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url).EndsWith("/documents/orders", StringComparison.Ordinal));
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assert.That(await page.Locator("#documents tbody tr").CountAsync()).IsEqualTo(3);
        var ascendingIds = await page.Locator("#documents .ts-data-doc-id").AllTextContentsAsync();
        await page.Locator("#documents thead th").First.Locator(".ts-data-sort").ClickAsync();
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url).EndsWith("/documents/orders?order=desc", StringComparison.Ordinal));
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assert.That(await page.Locator("#documents th[aria-sort='descending'] .ts-data-sort svg path").GetAttributeAsync("d")).IsEqualTo("m6 9 6 6 6-6");
        var descendingIds = await page.Locator("#documents .ts-data-doc-id").AllTextContentsAsync();
        await Assert.That(descendingIds.SequenceEqual(ascendingIds.Reverse())).IsTrue()
            .Because("Native descending IDs must reverse ascending IDs. Ascending: " + string.Join(",", ascendingIds) + "; descending: " + string.Join(",", descendingIds));
        await page.Locator("#documents thead th").First.Locator(".ts-data-sort").ClickAsync();
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url).EndsWith("/documents/orders", StringComparison.Ordinal));
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assert.That(await page.Locator("#documents th[aria-sort='ascending'] .ts-data-sort svg path").GetAttributeAsync("d")).IsEqualTo("m18 15-6-6-6 6");
        await page.Locator("#documents thead").GetByRole(AriaRole.Link, new() { Name = "total", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url).EndsWith("/documents/orders?sort=total", StringComparison.Ordinal));
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assert.That(await page.Locator("#documents th[aria-sort='ascending'] .ts-data-sort span").TextContentAsync()).IsEqualTo("total");
        await Assert.That((await page.Locator("#documents .ts-data-doc-id").AllTextContentsAsync()).SequenceEqual(new[] { ascendingIds[2], ascendingIds[0], ascendingIds[1] })).IsTrue();

        await NavigateAsync(database + "/sql");
        try
        {
            await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync(new() { Timeout = 15000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Format", Exact = true }).WaitForAsync(new() { Timeout = 15000 });
        }
        catch (TimeoutException)
        { throw new InvalidOperationException("The native SQL editor did not become ready: " + string.Join(" | ", bootFailures.Concat(errors))); }
        await Assert.That(await page.TitleAsync()).IsEqualTo("core-primary · SQL console · TerraScale");
        await Assert.That(await page.Locator("#sql").GetAttributeAsync("aria-hidden")).IsEqualTo("true");
        await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo("SELECT * FROM users LIMIT 20");
        await Assert.That(await page.Locator("#sql-analysis").InnerTextAsync()).Contains("EXPLAIN QUERY PLAN SELECT * FROM users LIMIT 20");
        await Assert.That((await page.Locator("#sql-analysis").InnerTextAsync()).Contains("Server check passed.", StringComparison.Ordinal)).IsFalse();
        await EditorAsync("SELECT * FROM orders WHERE total > 500 ORDER BY createdAt");
        await page.GetByRole(AriaRole.Button, new() { Name = "Format", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.getElementById('sql').value.includes('\\nFROM orders')");
        await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo("SELECT *\nFROM orders\nWHERE total > 500\nORDER BY createdAt");
        await CheckAsync();
        var analysis = await page.Locator("#sql-analysis").InnerTextAsync();
        await Assert.That(analysis).Contains("Add LIMIT to bound results.");
        await Assert.That(analysis).Contains("Server check passed.");
        await Assert.That(analysis).Contains("WHERE filters on total; consider an index for this field.");
        await Assert.That(analysis).Contains("ORDER BY sorts on createdAt; consider an index for bounded pagination.");
        await Assert.That(await page.Locator("#sql-result").CountAsync()).IsEqualTo(0)
            .Because("The native check validates and advises without executing the query.");
        await EditorAsync("DELETE FROM orders");
        await CheckAsync(422);
        await Assert.That(await page.Locator("#sql-analysis").CountAsync()).IsEqualTo(0);
        // Editor live regions may boot after navigation; keep their alert collision deterministic.
        await page.Locator("#sql-editor").EvaluateAsync("""
            element => {
                for (let index = 0; index < 2; index++) {
                    const alert = document.createElement('div');
                    alert.className = 'monaco-alert';
                    alert.setAttribute('role', 'alert');
                    alert.setAttribute('aria-atomic', 'true');
                    element.append(alert);
                }
            }
            """);
        var sqlAlert = page.Locator(".ts-data-sql .ts-alert-danger[role='alert']");
        await Assert.That(await sqlAlert.CountAsync()).IsEqualTo(1);
        await Assert.That(await sqlAlert.InnerTextAsync()).IsEqualTo("Only SELECT, WITH, and EXPLAIN statements are allowed.");
        await Assert.That(errors.ToArray()).IsEmpty();
        await Assert.That(bootFailures.ToArray()).IsEmpty();

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200);
        }
        async Task EditorAsync(string statement)
        {
            await page.Locator("#sql").EvaluateAsync("(el, text) => { el.value = text; el.dispatchEvent(new Event('input', {bubbles:true})); }", statement);
            await Assert.That(await page.Locator("#sql").InputValueAsync()).IsEqualTo(statement);
        }
        async Task CheckAsync(int expectedStatus = 200)
        {
            var response = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Check SQL", Exact = true }).ClickAsync(),
                value => value.Request.IsNavigationRequest && value.Request.Method == "POST" && new Uri(value.Url).AbsolutePath == "/api/v1/dashboard/data/sql/check");
            await Assert.That(response.Status).IsEqualTo(expectedStatus);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
    }
}
