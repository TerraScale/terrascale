using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class SqlResultsBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments(0, false)]
    [Arguments(201, false)]
    [Arguments(1001, false)]
    [Arguments(10000, false)]
    [Arguments(201, true)]
    public async Task ReturnedRowsPageAndExportWithoutRepeatingTheWrite(int count, bool blockMonaco)
    {
        var executions = 0;
        const string hostile = "</script><script>window.sqlCellExecuted=true</script><img src=x onerror='window.sqlCellExecuted=true'>";
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (http.Request.Method != "POST" || !http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) return null;
            if (await DatabaseSdkScenario.EmptySqlCatalogAsync(http) is { } catalog) return catalog;
            Interlocked.Increment(ref executions);
            return Results.Json(new
            {
                results = new[] { new
                {
                    columns = new[] { new { name = "n", decltype = "INTEGER" }, new { name = "n", decltype = "TEXT" }, new { name = "amount", decltype = "INTEGER" }, new { name = "text", decltype = "TEXT" }, new { name = "empty", decltype = "TEXT" } },
                    rows = Enumerable.Range(0, count).Select(index => new object?[] { 9007199254740993L + index, null, -3, index == 0 ? "=1+1,\"quoted\"\nline" : index == 1 ? hostile : "null", "" }).ToArray(),
                    rows_affected = count, last_insert_rowid = (long?)null
                } }
            });
        });
        using var client = scenario.CreateClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-export");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { Permissions = ["clipboard-read", "clipboard-write"] });
        if (blockMonaco) await context.RouteAsync("**/js/monaco.*", route => route.AbortAsync());
        var page = await context.NewPageAsync();
        var editorFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Console += (_, message) => { if (message.Text.StartsWith("Code editor unavailable:", StringComparison.Ordinal)) editorFailed.TrySetResult(); };
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: account.Email, password: account.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        await page.GotoAsync(new Uri(scenario.Origin, "/databases/" + id + "/sql").ToString());
        if (blockMonaco)
        {
            await editorFailed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await page.Locator("#sql").FillAsync("INSERT INTO audit VALUES (1) RETURNING *");
        }
        else
        {
            await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync();
            await page.Locator("#sql").EvaluateAsync("el => { el.value = 'INSERT INTO audit VALUES (1) RETURNING *'; el.dispatchEvent(new Event('input', {bubbles:true})); }");
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Run query", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Download JSON", Exact = true }).WaitForAsync();
        await Assert.That(await page.Locator("#sql-result tbody tr").CountAsync()).IsEqualTo(Math.Min(200, count));
        if (blockMonaco) await Assert.That(await page.Locator("#sql").IsVisibleAsync()).IsTrue();
        if (count > 1) await Assert.That(await page.Locator("#sql-result tbody tr").Nth(1).Locator("td").Nth(3).InnerTextAsync()).IsEqualTo(hostile);
        await Assert.That(await page.Locator("#sql-result img, #sql-result script:not([type='application/json'])").CountAsync()).IsEqualTo(0);
        await Assert.That(await page.EvaluateAsync<bool>("window.sqlCellExecuted === undefined")).IsTrue();
        if (count > 200)
        {
            var pager = page.GetByRole(AriaRole.Navigation, new() { Name = "SQL result pages" });
            await pager.FocusAsync();
            await pager.PressAsync("End");
            var start = (count - 1) / 200 * 200;
            await Assert.That(await page.Locator("[data-sql-range]").InnerTextAsync()).IsEqualTo($"Rows {start + 1}–{count} of {count}");
            await Assert.That(await page.Locator("#sql-result tbody tr").CountAsync()).IsEqualTo(count - start);
            await pager.PressAsync("Home");
            await pager.PressAsync("ArrowRight");
            await Assert.That(await page.Locator("[data-sql-range]").InnerTextAsync()).IsEqualTo($"Rows 201–{Math.Min(400, count)} of {count}");
        }
        await page.EvaluateAsync("() => { window.resultUrls = new Set(); const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL); URL.createObjectURL = blob => { const url = create(blob); window.resultUrls.add(url); return url; }; URL.revokeObjectURL = url => { window.resultUrls.delete(url); revoke(url); }; }");
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Download JSON", Exact = true }).ClickAsync());
        var jsonText = await File.ReadAllTextAsync((await download.PathAsync())!);
        using var json = JsonDocument.Parse(jsonText);
        await Assert.That(json.RootElement.GetProperty("rows").GetArrayLength()).IsEqualTo(count);
        await Assert.That(json.RootElement.GetProperty("columns").EnumerateArray().Count(column => column.GetProperty("name").GetString() == "n")).IsEqualTo(2);
        if (count > 1) await Assert.That(json.RootElement.GetProperty("rows")[1][3].GetString()).IsEqualTo(hostile);
        if (count > 0)
        {
            var first = json.RootElement.GetProperty("rows")[0];
            await Assert.That(first[0].GetInt64()).IsEqualTo(9007199254740993L);
            await Assert.That(first[1].ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(first[4].GetString()).IsEqualTo("");
        }
        download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Download CSV", Exact = true }).ClickAsync());
        var csv = await File.ReadAllTextAsync((await download.PathAsync())!);
        if (count > 0) await Assert.That(csv).Contains("9007199254740993,\\N,-3,\"'=1+1,\"\"quoted\"\"\nline\",\"\"");
        if (count > 1) await Assert.That(csv).Contains(hostile);
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy JSON", Exact = true }).ClickAsync();
        await page.GetByText("JSON copied.", new() { Exact = true }).WaitForAsync();
        await Assert.That(await page.EvaluateAsync<string>("navigator.clipboard.readText()")).IsEqualTo(jsonText);
        await page.WaitForFunctionAsync("() => window.resultUrls.size === 0");
        await page.EvaluateAsync("() => { navigator.clipboard.writeText = async () => { throw new Error('denied'); }; URL.createObjectURL = () => { throw new Error('blocked'); }; }");
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy JSON", Exact = true }).ClickAsync();
        await page.GetByText("Could not copy JSON. Try Download JSON instead.", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Download JSON", Exact = true }).ClickAsync();
        await page.GetByText("Could not start the download. Try copying JSON instead.", new() { Exact = true }).WaitForAsync();
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(1);
    }
}
