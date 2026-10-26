using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Data;

// Allowlists and the expiring-soon notice through the dashboard's own routes, against the test ts-auth.
[ClassDataSource<DashboardRootFixture>]
public sealed class ApiKeyNetworkJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("json", "refused")]
    [Arguments("html", "refused")]
    [Arguments("mcp", "refused")]
    [Arguments("html", "canceled")]
    [Arguments("html", "timeout")]
    public async Task IssuedSecretSurvivesFailedSupplementaryWritesAndFailedListReads(string surface, string failure)
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        PostCommitFaults faults = null!;
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
        {
            var transport = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
            faults = new(environment.State, transport, failure);
            builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(faults));
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(faults));
        });
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client);
        if (surface == "mcp")
        {
            var pat = await HttpJourney.CreatePatAsync(client);
            var reply = await HttpJourney.McpAsync(client, pat, "tools/call", new
            { name = "create_api_key", arguments = new { name = "After commit", scopes = new[] { "documents:read" } } });
            var result = reply.GetProperty("result").GetProperty("structuredContent");
            await Assert.That(result.GetProperty("secret").GetString() == environment.ApiKeys.Issued().Single().Secret).IsTrue();
        }
        else
        {
            if (surface == "html") client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key?kind=key", new()
            { ["_csrf"] = csrf, ["name"] = "After commit", ["scopes"] = "documents:read" });
            await Assert.That(response.StatusCode).IsEqualTo(surface == "html" ? HttpStatusCode.OK : HttpStatusCode.Created);
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(body.Contains(environment.ApiKeys.Issued().Single().Secret, StringComparison.Ordinal)).IsTrue();
            if (surface == "html")
            {
                await Assert.That(body.Contains("key change succeeded", StringComparison.Ordinal)).IsTrue();
                await Assert.That(body.Contains("complete key list is unavailable", StringComparison.Ordinal)).IsTrue();
                var key = environment.ApiKeys.Issued().Single().Key;
                using var rotated = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/rotate", new()
                { ["_csrf"] = csrf, ["keyId"] = key.Id, ["previousSecretTtl"] = "0" });
                await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(rotated.Headers.CacheControl?.NoStore).IsTrue();
                var rotatedBody = await rotated.Content.ReadAsStringAsync();
                await Assert.That(rotatedBody.Contains(environment.ApiKeys.Issued().Single().Secret, StringComparison.Ordinal)).IsTrue();
                await Assert.That(rotatedBody.Contains("key change succeeded", StringComparison.Ordinal)).IsTrue();
            }
        }
        await Assert.That(faults.FailedAuditWrites).IsEqualTo(1);
        await Assert.That(faults.IssuerWrites).IsEqualTo(surface == "html" ? 2 : 1);
        await Assert.That(environment.ApiKeys.Issued().Length).IsEqualTo(1);
        using var fresh = await client.GetAsync("/api-keys");
        await Assert.That((await fresh.Content.ReadAsStringAsync()).Contains(environment.ApiKeys.Issued().Single().Secret, StringComparison.Ordinal)).IsFalse();
    }

    private sealed class PostCommitFaults(IDashboardState state, IHttpClientFactory transport, string failure) : IDashboardState, IHttpClientFactory
    {
        public string ReadFailure => failure;
        public int IssuerWrites;
        public int FailedAuditWrites;
        public HttpClient CreateClient(string name) => new(new FaultTransport(transport.CreateClient(name), this));
        public Task InitializeAsync(CancellationToken ct) => state.InitializeAsync(ct);
        public Task CheckReadyAsync(CancellationToken ct) => state.CheckReadyAsync(ct);
        public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken ct) => state.GetAsync(collection, id, ct);
        public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken ct) => state.ListAsync(collection, ct);
        public Task DeleteAsync(string collection, string id, string version, CancellationToken ct) => state.DeleteAsync(collection, id, version, ct);
        public Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken ct)
        {
            if (IssuerWrites > 0 && collection == AccountService.OrganizationsCollection)
            { FailedAuditWrites++; throw new HttpRequestException("Synthetic supplementary write outage."); }
            return state.PutAsync(collection, id, document, precondition, ct);
        }
        private sealed class FaultTransport(HttpClient inner, PostCommitFaults faults) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (faults.IssuerWrites > 0 && request.Method == HttpMethod.Get && path.EndsWith("/api-keys", StringComparison.Ordinal))
                    throw faults.ReadFailure switch
                    {
                        "canceled" => new OperationCanceledException("Synthetic dependency timeout."),
                        "timeout" => new TimeoutException("Synthetic dependency timeout."),
                        _ => (Exception)new AccountAccessException("Synthetic post-commit list refusal.")
                    };
                var response = await HttpForwarding.SendAsync(inner, request, ct);
                if (response.IsSuccessStatusCode && request.Method == HttpMethod.Post &&
                    (path.EndsWith("/api-keys", StringComparison.Ordinal) || path.EndsWith("/rotate", StringComparison.Ordinal))) faults.IssuerWrites++;
                return response;
            }
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }
    }

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
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
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
        await page.Locator("#key-expiry").SelectOptionAsync("30");
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
        await Assert.That(await row.Locator("td.ts-apikeys-date").Nth(1).IsVisibleAsync()).IsTrue();
        await Assert.That(await row.Locator("td.ts-apikeys-date").Nth(1).InnerTextAsync()).Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} UTC$");
        await Assert.That(await row.Locator("td.ts-apikeys-date").Nth(2).IsVisibleAsync()).IsTrue();
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
        var done = page.Locator(".ts-apikeys-secret").GetByRole(AriaRole.Link, new() { Name = "Done", Exact = true });
        var doneQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(new Uri(page.Url), (await done.GetAttributeAsync("href"))!).Query);
        await Assert.That(doneQuery["organizationId"].ToString()).IsNotEmpty();
        await Assert.That(doneQuery["projectId"].ToString()).IsNotEmpty();
        await done.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForLoadStateAsync();
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
        await Assert.That(issued.RootElement.GetProperty("apiKey").GetProperty("createdByUserId").GetString())
            .IsEqualTo(environment.ApiKeys.Issued().Single().Key.CreatedBy);
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
