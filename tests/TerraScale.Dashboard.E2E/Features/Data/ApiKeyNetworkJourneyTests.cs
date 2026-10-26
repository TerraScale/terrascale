using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Data;

// Allowlists and the expiring-soon notice through the dashboard's own routes, against the test ts-auth.
[ClassDataSource<DashboardRootFixture>]
public sealed class ApiKeyNetworkJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Category("Slow")]
    [Arguments("missing")]
    [Arguments("rejected")]
    [Arguments("throws")]
    [Arguments("success")]
    public async Task RevealedSecretAnnouncesCopyOutcomeAndRemainsKeyboardCopyable(string clipboard)
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
        await app.StartAsync();
        var origin = new Uri(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var behavior = clipboard switch
        {
            "missing" => "undefined",
            "rejected" => "{ writeText: () => Promise.reject(new Error('Denied')) }",
            "throws" => "{ writeText: () => { throw new Error('Unavailable'); } }",
            _ => "{ writeText: text => { window.copiedTestKey = text; return Promise.resolve(); } }"
        };
        await context.AddInitScriptAsync("Object.defineProperty(navigator, 'clipboard', { configurable: true, value: " + behavior + " });");
        var page = await context.NewPageAsync();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        await BrowserAudit.SignInAsync(page, origin, "/api-keys?kind=key&create=key");
        await page.Locator("#create-key[open]").WaitForAsync();
        await page.Locator("#key-name").FillAsync("Clipboard journey");
        var response = await page.RunAndWaitForResponseAsync(
            () => page.Locator("#create-key").GetByRole(AriaRole.Button, new() { Name = "Create key", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && response.Url.Contains("/api/v1/dashboard/credentials/key", StringComparison.Ordinal));
        await Assert.That(response.Headers["cache-control"]).Contains("no-store");
        var secret = page.Locator("#api-key-secret");
        await secret.WaitForAsync();
        await page.WaitForLoadStateAsync();
        await Assert.That(await secret.InnerTextAsync()).IsNotEmpty();
        var row = page.Locator("#credentials tbody tr").Filter(new() { HasText = "Clipboard journey" });
        await Assert.That(await row.GetByRole(AriaRole.Cell, new() { Name = "user@example.com", Exact = true }).CountAsync()).IsEqualTo(1);
        await Assert.That(await page.EvaluateAsync<bool>("""
            () => {
                const secret = document.getElementById('api-key-secret').textContent;
                return !location.href.includes(secret) && ![...document.querySelectorAll('*')].some(el =>
                    [...el.attributes].some(attr => attr.value.includes(secret)) ||
                    (el.tagName === 'SCRIPT' && el.textContent.includes(secret)));
            }
            """)).IsTrue();
        var copy = page.Locator(".ts-apikeys-secret").GetByRole(AriaRole.Button, new() { Name = "Copy", Exact = true });
        await copy.ClickAsync();
        var expected = clipboard == "success" ? "API key copied." : "Copy failed. Secret selected; press Ctrl+C or ⌘C to copy manually.";
        await page.WaitForFunctionAsync("expected => document.querySelector('.ts-apikeys-secret [role=status]').textContent === expected", expected);
        if (clipboard == "success")
            await Assert.That(await page.EvaluateAsync<bool>("() => window.copiedTestKey === document.getElementById('api-key-secret').textContent")).IsTrue();
        else
            await Assert.That(await secret.EvaluateAsync<bool>("el => document.activeElement === el && getSelection().toString() === el.textContent")).IsTrue();
        // Manual selection is available through normal keyboard navigation even after a successful copy.
        await copy.FocusAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Assert.That(await secret.EvaluateAsync<bool>("el => document.activeElement === el && getSelection().toString() === el.textContent")).IsTrue();
        await Assert.That(errors).IsEmpty();
        await page.ReloadAsync();
        await Assert.That(await page.Locator(".ts-apikeys-secret").CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task KeyAndProjectAllowlistsRoundTripAndAKeyExpiringThisWeekNotifiesOnce()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client);

        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        { ["_csrf"] = csrf, ["name"] = "Office key", ["scopes"] = "documents:read", ["allowedCidrs"] = "203.0.113.0/24\n198.51.100.7" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var keyId = issued.RootElement.GetProperty("apiKey").GetProperty("keyId").GetString()!;
        await Assert.That(TerraScaleIds.IsValid(issued.RootElement.GetProperty("apiKey").GetProperty("createdByUserId").GetString()!)).IsTrue();
        await Assert.That(issued.RootElement.GetProperty("apiKey").GetProperty("createdByEmail").GetString()).IsEqualTo("user@example.com");
        await Assert.That(Cidrs(issued.RootElement.GetProperty("apiKey"))).IsEquivalentTo(new[] { "203.0.113.0/24", "198.51.100.7/32" });

        using var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/allowed-cidrs", new()
        { ["_csrf"] = csrf, ["keyId"] = keyId, ["allowedCidrs"] = "10.0.0.0/33" });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("allowed_cidrs must hold up to 32 valid IPs or CIDR ranges");
        using var replaced = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/allowed-cidrs", new()
        { ["_csrf"] = csrf, ["keyId"] = keyId, ["allowedCidrs"] = "2001:db8::/48" });
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var listed = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/credentials");
        await Assert.That(Cidrs(listed.EnumerateArray().Single())).IsEquivalentTo(new[] { "2001:db8::/48" });
        await Assert.That(listed.EnumerateArray().Single().GetProperty("createdByUserId").GetString())
            .IsEqualTo(issued.RootElement.GetProperty("apiKey").GetProperty("createdByUserId").GetString());
        await Assert.That(listed.EnumerateArray().Single().GetProperty("createdByEmail").GetString()).IsEqualTo("user@example.com");

        using var network = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/project-allowed-cidrs", new()
        { ["_csrf"] = csrf, ["allowedCidrs"] = "192.0.2.0/24" });
        await Assert.That(network.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        environment.ApiKeys.ExpireAt(keyId, DateTimeOffset.UtcNow.AddDays(3));
        var page = await client.GetStringAsync("/api-keys");
        await Assert.That(page).Contains("Network access");
        await Assert.That(page).Contains("192.0.2.0/24");
        await Assert.That(page).Contains("2001:db8::/48");
        await Assert.That(page).Contains("Expires soon");
        await Assert.That(page).Contains("Revoking stops this key everywhere within 2 seconds: new requests are refused and open connections (including Postgres) are closed within 3 seconds.");
        await Assert.That(page).Contains("Rotating issues a new secret, shown once. With no grace, the old secret and its tokens stop everywhere within 2 seconds; open connections (including Postgres) close within 3 seconds. With a grace period, the old secret works until it ends; these bounds start then.");
        await Assert.That(page).Contains(">No grace period</option>");
        await Assert.That(page.Contains("tokens it already obtained keep working", StringComparison.Ordinal)).IsFalse();

        await client.GetStringAsync("/notifications");
        var inbox = await client.GetStringAsync("/notifications");
        await Assert.That(inbox.Split("API key Office key expires on ").Length - 1).IsEqualTo(1);
    }

    private static string[] Cidrs(JsonElement key) => key.GetProperty("allowedCidrs").EnumerateArray().Select(item => item.GetString()!).ToArray();
}
