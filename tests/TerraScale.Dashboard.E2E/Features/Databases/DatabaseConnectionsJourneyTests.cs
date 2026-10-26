using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class DatabaseConnectionsJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task OverviewCopiesDatabaseScopedProtocolsAndReportsClipboardFailures(int width, int height)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var identity = await client.GetAsync("/api/v1/auth/me");
        using var context = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
        var project = context.RootElement.GetProperty("projectId").GetString()!;
        const string name = "connect-overview";
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = database.RootElement.GetProperty("id").GetString()!;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await browserContext.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: account.Email, password: account.Password);
        await page.GotoAsync(new Uri(scenario.Origin, "/databases/" + id).ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy Document HTTP URL", Exact = true }).WaitForAsync();
        await page.EvaluateAsync("""
            () => Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
              writeText: text => window.__copyDenied
                ? Promise.reject(new DOMException('Denied', 'NotAllowedError'))
                : (window.__copiedUrl = text, Promise.resolve())
            } })
            """);
        var endpoint = $"https://api.terrascale.tech/v1/projects/{project}/databases/{name}";
        var protocols = new[]
        {
            (Id: "document", Label: "Document HTTP", Url: endpoint + "/collections"),
            (Id: "sql", Label: "HTTP SQL", Url: endpoint + "/sql/execute"),
            (Id: "libsql", Label: "libSQL / Hrana", Url: endpoint + "/libsql/"),
            (Id: "postgres", Label: "Postgres", Url: $"postgresql://app@pg.terrascale.tech:5432/{project}%2F{name}?sslmode=require")
        };
        var card = page.Locator("#connect");
        await Assert.That(await card.InnerTextAsync()).Contains("Project ID");
        await Assert.That(await card.InnerTextAsync()).Contains("expires_in");
        await Assert.That(await card.InnerTextAsync()).Contains("no token exchange");
        await Assert.That(await card.InnerTextAsync()).Contains("sslmode=verify-full&sslrootcert=system");
        await Assert.That(await card.Locator("a[href^='/api-keys']").CountAsync()).IsEqualTo(1);
        foreach (var protocol in protocols)
        {
            await Assert.That(await card.Locator("#db-connect-" + protocol.Id).TextContentAsync()).IsEqualTo(protocol.Url);
            var button = card.GetByRole(AriaRole.Button, new() { Name = "Copy " + protocol.Label + " URL", Exact = true });
            // Native buttons must activate from the keyboard as well as a pointer.
            await button.FocusAsync();
            await button.PressAsync("Enter");
            await page.WaitForFunctionAsync("id => document.getElementById(id).textContent === 'Copied'", "db-connect-" + protocol.Id + "-status");
            await Assert.That(await page.EvaluateAsync<string>("() => window.__copiedUrl")).IsEqualTo(protocol.Url);
            await page.EvaluateAsync("() => window.__copyDenied = true");
            await button.ClickAsync();
            await page.WaitForFunctionAsync("id => document.getElementById(id).textContent.includes('Copy failed')", "db-connect-" + protocol.Id + "-status");
            var status = card.Locator("#db-connect-" + protocol.Id + "-status");
            await Assert.That(await status.GetAttributeAsync("role")).IsEqualTo("status");
            await Assert.That(await status.InnerTextAsync()).Contains("Select and copy the URL manually.");
            await page.EvaluateAsync("() => window.__copyDenied = false");
            await button.ClickAsync();
            await page.WaitForFunctionAsync("id => document.getElementById(id).textContent === 'Copied'", "db-connect-" + protocol.Id + "-status");
        }
        // Insecure/unsupported environments may have no Clipboard API at all.
        await page.EvaluateAsync("() => Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined })");
        await card.GetByRole(AriaRole.Button, new() { Name = "Copy Document HTTP URL", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.getElementById('db-connect-document-status').textContent.includes('Copy failed')");
        await Assert.That(errors).IsEmpty();
        using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = name });
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }
}
