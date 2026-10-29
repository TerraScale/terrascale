using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform
{
    [ClassDataSource<DashboardE2EFixture>]
    [Category("Slow")]
    public sealed class DashboardBrowserJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task DatabaseProfileBillingAndDatastarDesktopAndMobile()
        {
            var output = BrowserAudit.Output(fixture.Root, "journey");
            var origin = fixture.Origin.ToString().TrimEnd('/');
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            IPage? activePage = null;
            try
            {
                foreach (var (size, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
                {
                    // Closed explicitly on success only, so a failure can still capture the open page below.
                    var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
                    var page = activePage = await context.NewPageAsync();
                    var errors = new ConcurrentQueue<string>();
                    page.PageError += (_, error) => errors.Enqueue(error);
                    var name = $"qa-{size}-{Guid.NewGuid().ToString("N")[..10]}";
                    await BrowserAudit.SignInAsync(page, fixture.Origin);
                    await GotoAsync("/");
                    var personalOrg = (await (await context.APIRequest.GetAsync(origin + "/api/v1/auth/me")).JsonAsync())!.Value.GetProperty("organizationId").GetString()!;
                    await ShotAsync("home");
                    await GotoAsync("/profile");
                    await page.Locator("#displayName").FillAsync("QA Test User");
                    await SubmitAsync("form:is([action=\"/api/v1/dashboard/profile\"],[action^=\"/api/v1/dashboard/profile?\"])", "Save profile");
                    await Assert.That(await page.Locator("#displayName").InputValueAsync()).IsEqualTo("QA Test User");
                    await page.Locator("#displayName").FillAsync("Test User");
                    await SubmitAsync("form:is([action=\"/api/v1/dashboard/profile\"],[action^=\"/api/v1/dashboard/profile?\"])", "Save profile");

                    await GotoAsync("/databases");
                    await page.GetByRole(AriaRole.Button, new() { Name = "New database", Exact = true }).First.ClickAsync();
                    await page.Locator("#create-database[open]").WaitForAsync();
                    foreach (var invalid in new[] { "Orders", "-orders", "orders-" })
                    {
                        await page.Locator("#database-name").FillAsync(invalid);
                        await Assert.That(await page.Locator("#database-name").EvaluateAsync<bool>("el => el.validity.patternMismatch")).IsTrue().Because("Native database names reject uppercase and edge hyphens");
                    }
                    await page.Locator("#database-name").FillAsync(name);
                    await Assert.That(await page.Locator("#database-name").EvaluateAsync<bool>("el => el.checkValidity()")).IsTrue().Because("Native database names accept inner hyphens");
                    var map = page.Locator("#create-database [data-select-id=\"database-region\"]");
                    var sjc = page.Locator("#database-region input[value=\"SJC\"]");
                    var sjcInitially = await sjc.IsCheckedAsync();
                    await map.GetByRole(AriaRole.Button, new() { Name = "San Francisco Bay Area (SJC)", Exact = true }).ClickAsync();
                    await Assert.That(await sjc.IsCheckedAsync()).IsEqualTo(!sjcInitially).Because("Map picks change the canonical native region checkbox");
                    const string pressed = "expected => document.querySelector('#create-database button[data-region=\"SJC\"]').getAttribute('aria-pressed') === String(expected)";
                    await page.WaitForFunctionAsync(pressed, !sjcInitially);
                    await sjc.SetCheckedAsync(sjcInitially);
                    await page.WaitForFunctionAsync(pressed, sjcInitially);
                    await page.Locator("#create-database input[name=\"region\"][value=\"GRU\"]").CheckAsync();
                    await sjc.CheckAsync();
                    await SubmitAsync("#create-database form", "Create database");
                    var databaseId = new Uri(page.Url).AbsolutePath.Split('/').ElementAtOrDefault(2);
                    await Assert.That(databaseId).IsNotNullOrEmpty().Because("Created database opens its details");
                    await ShotAsync("database");
                    await GotoAsync($"/databases/{databaseId}/documents");
                    await page.GetByRole(AriaRole.Button, new() { Name = "New collection", Exact = true }).Last.ClickAsync();
                    await page.Locator("#new-collection[open]").WaitForAsync();
                    await page.Locator("#new-collection-name").FillAsync("users");
                    await SubmitAsync("#new-collection form", "Create collection");
                    await page.GetByRole(AriaRole.Button, new() { Name = "Add document", Exact = true }).ClickAsync();
                    await page.Locator("#add-document[open]").WaitForAsync();
                    await page.Locator("#new-document-id").FillAsync("01K78D0C000000000000000020");
                    await EditorAsync("#new-document-json", """{"name":"Ada","score":7,"nested":{"keep":true,"remove":false}}""");
                    await SubmitAsync("#add-document form", "Add document");
                    await page.GetByRole(AriaRole.Link, new() { Name = "01K78D0C000000000000000020", Exact = true }).ClickAsync();
                    await page.Locator("#edit-document[open]").WaitForAsync();
                    await page.Locator("#operation").SelectOptionAsync("patch");
                    await EditorAsync("#document-json", """{"score":9,"nested":{"remove":null}}""");
                    await SubmitAsync("#edit-document form", "Save document");
                    await page.GetByRole(AriaRole.Link, new() { Name = "01K78D0C000000000000000020", Exact = true }).ClickAsync();
                    await page.Locator("#edit-document[open]").WaitForAsync();
                    var patched = JsonNode.Parse(await page.Locator("#document-json").InputValueAsync());
                    await Assert.That(JsonNode.DeepEquals(patched, JsonNode.Parse("""{"name":"Ada","score":9,"nested":{"keep":true}}"""))).IsTrue()
                        .Because("Patch preserves unrelated fields and removes null members: " + patched?.ToJsonString());
                    await page.Locator("#edit-document").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                    await ShotAsync("documents");
                    await GotoAsync($"/databases/{databaseId}/schema?collection=users");
                    await Assert.That(await page.Locator("#declared-schema").InputValueAsync()).IsEqualTo("{}").Because("Collections start without declared validation");
                    await Assert.That(await page.Locator("main").InnerTextAsync()).Contains("No declared validation schema.");
                    await Assert.That(await page.Locator("section[aria-labelledby=inferred-schema-title] pre").InnerTextAsync()).Contains("score").Because("Inferred fields reflect live documents separately from declared validation");
                    await ShotAsync("schema");
                    await GotoAsync($"/databases/{databaseId}/sql?collection=users");
                    await EditorAsync("#sql", "SELECT * FROM users LIMIT 100");
                    await SubmitAsync("form:is([action=\"/api/v1/dashboard/data/sql\"],[action^=\"/api/v1/dashboard/data/sql?\"])", "Run query");
                    await page.GetByText("Ada", new() { Exact = true }).WaitForAsync();
                    await ShotAsync("sql");

                    await GotoAsync("/api-keys?create=key");
                    await page.Locator("#create-key[open]").WaitForAsync();
                    await page.Locator("#key-name").FillAsync(name);
                    await page.Locator("#key-database").SelectOptionAsync(databaseId!);
                    await page.Locator("#key-access-write").CheckAsync();
                    await Assert.That(await page.Locator("#key-name").InputValueAsync()).IsEqualTo(name);
                    await Assert.That(await page.Locator("#key-database").InputValueAsync()).IsEqualTo(databaseId);
                    await Assert.That(await page.Locator("#key-access-write").IsCheckedAsync()).IsTrue();
                    await BrowserAudit.SubmitConfirmedAsync(page, fixture.Origin, () => page.Locator("#create-key form").GetByRole(AriaRole.Button, new() { Name = "Create key", Exact = true }).ClickAsync());
                    var keySecret = page.Locator(".ts-apikeys-secret-value");
                    await keySecret.WaitForAsync();
                    var issuedKey = await keySecret.InnerTextAsync();
                    await Assert.That(issuedKey).Matches("^[0-9A-HJKMNP-TV-Z]{26}_[0-9a-hjkmnp-tv-z]{52}$").Because("ts-auth reveals its canonical project key once");
                    await ShotAsync("key-created");
                    await GotoAsync("/api-keys?kind=key");
                    await Assert.That(await page.Locator("main").InnerTextAsync()).DoesNotContain(issuedKey).Because("API-key plaintext disappears on refresh");
                    var keyRow = page.Locator("#credentials tbody tr").Filter(new() { HasText = name });
                    await keyRow.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
                    await page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Revoke credential", Exact = true }).ClickAsync();
                    await page.WaitForURLAsync(new Regex("/api-keys"));
                    await Assert.That(await page.Locator("#credentials tbody tr").Filter(new() { HasText = name }).CountAsync()).IsEqualTo(0);

                    await GotoAsync("/profile?tab=access-tokens");
                    await page.Locator("#accessTokenName").FillAsync(name);
                    // Creating a token confirms the action, then signs in again through the hosted page; the callback renders the secret once.
                    await BrowserAudit.SubmitConfirmedAsync(page, fixture.Origin, () => page.Locator("#profile-access-token-create form").GetByRole(AriaRole.Button, new() { Name = "Create token", Exact = true }).ClickAsync());
                    await page.GetByRole(AriaRole.Link, new() { Name = "Continue as Test User", Exact = true }).ClickAsync();
                    var secret = await page.Locator("#access-token-secret").InputValueAsync();
                    await Assert.That(secret).Matches("^tspat_[A-Za-z0-9_-]{43}$");
                    var mcp = await McpAsync(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "list_databases", arguments = new { } } });
                    await Assert.That(mcp.Status).IsEqualTo(200);
                    await Assert.That(await mcp.TextAsync()).Contains(name).Because("MCP observes the database created through native UI");
                    await GotoAsync("/profile?tab=access-tokens");
                    await Assert.That(await page.Locator("#access-token-secret").CountAsync()).IsEqualTo(0);
                    await Assert.That(await page.Locator("#profile-access-tokens").GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).CountAsync()).IsEqualTo(0).Because("revocation lives only on the danger tab");
                    await GotoAsync("/profile?tab=danger");
                    await Assert.That(await page.Locator(".ts-profile-tabs .ts-tab").Last.InnerTextAsync()).Contains("Danger").Because("the danger zone is the last profile tab");
                    var tokenRow = page.Locator("#profile-danger tbody tr").Filter(new() { HasText = name });
                    await tokenRow.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
                    await page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Revoke token", Exact = true }).ClickAsync();
                    await page.WaitForURLAsync(new Regex(@"/profile\?tab=danger"));
                    var revoked = await McpAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
                    await Assert.That(revoked.Status).IsEqualTo(401);

                    await GotoAsync("/organization?tab=billing");
                    await ShotAsync("billing");
                    await GotoAsync($"/databases/{databaseId}?delete=1");
                    await page.Locator("dialog[open] input[name=\"confirmName\"]").FillAsync(name);
                    await SubmitAsync("dialog[open] form", "Delete database");
                    await GotoAsync("/databases");
                    await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true }).CountAsync()).IsEqualTo(0);
                    if (size == "desktop")
                    {
                        await GotoAsync("/organization?newOrganization=organization");
                        await page.Locator("#create-organization[open]").WaitForAsync();
                        await page.Locator("#create-organization input[name=\"name\"]").FillAsync(name + " company");
                        await SubmitAsync("#create-organization form", "Create organization");
                        await page.GetByRole(AriaRole.Heading, new() { Name = "Set up billing", Exact = true }).WaitForAsync();
                        await SubmitAsync("#create-organization form", "Continue to checkout");
                        await page.GetByRole(AriaRole.Heading, new() { Name = "Create the first project", Exact = true }).WaitForAsync();
                        await page.Locator("#create-organization input[name=\"name\"]").FillAsync("Production");
                        await page.Locator("#create-organization select[name=\"regions\"]").SelectOptionAsync(new[] { "GRU", "SJC" });
                        await page.Locator("#create-organization select[name=\"region\"]").SelectOptionAsync("GRU");
                        await page.Locator("#create-organization select[name=\"backupRegion\"]").SelectOptionAsync("SJC");
                        await SubmitAsync("#create-organization form", "Create project");
                        await GotoAsync("/organization?tab=billing");
                        await page.GetByText("Starter", new() { Exact = true }).First.WaitForAsync();
                        await Assert.That(await page.Locator("#billing-summary").InnerTextAsync()).Contains("On file").Because("Stripe confirmation activates the bound organization");
                        await Assert.That(await page.Locator("form:is([action=\"/billing/checkout\"],[action^=\"/billing/checkout?\"])").CountAsync()).IsEqualTo(0).Because("Active paid subscriptions use the customer portal");
                        await ShotAsync("paid-billing");
                        await page.GetByRole(AriaRole.Link, new() { Name = "Manage payment", Exact = true }).ClickAsync();
                        await page.GetByRole(AriaRole.Heading, new() { Name = "Test-only Stripe customer portal" }).WaitForAsync();
                        await GotoAsync("/organization");
                        var csrf = await page.Locator("input[name=\"_csrf\"]").First.InputValueAsync();
                        var form = context.APIRequest.CreateFormData();
                        form.Set("_csrf", csrf);
                        form.Set("organizationId", personalOrg);
                        form.Set("returnUrl", "/");
                        var switched = await context.APIRequest.PostAsync(origin + "/api/v1/dashboard/switch-organization", new() { Form = form, MaxRedirects = 0 });
                        await Assert.That(switched.Status).IsEqualTo(303).Because("Organization selection restores the personal scope");
                    }
                    await Assert.That(errors).IsEmpty().Because($"{size}: no browser runtime errors");
                    await context.CloseAsync();

                    async Task GotoAsync(string path, int status = 200)
                    {
                        var response = await page.GotoAsync(origin + path);
                        await Assert.That(response!.Status).IsEqualTo(status).Because(path);
                        await page.EvaluateAsync("() => document.fonts.ready");
                    }
                    // Mirrors waitForNavigation: forms post to varying endpoints, so there is no single URL to wait for.
#pragma warning disable CS0612
                    Task SubmitAsync(string form, string button) => page.RunAndWaitForNavigationAsync(
                        () => page.Locator(form).GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync());
#pragma warning restore CS0612
                    async Task EditorAsync(string id, string value)
                    {
                        var textarea = page.Locator(id);
                        await textarea.EvaluateAsync("(el, text) => { el.value = text; el.dispatchEvent(new Event('input', { bubbles: true })); }", value);
                        await Assert.That(await textarea.InputValueAsync()).IsEqualTo(value);
                    }
                    Task ShotAsync(string key) => page.ScreenshotAsync(new() { Path = Path.Combine(output, $"{key}-{size}.png"), FullPage = true, Caret = ScreenshotCaret.Hide, Mask = [page.Locator(".ts-apikeys-secret-value, #access-token-secret, input[type=password]")] });
                    Task<IAPIResponse> McpAsync(object rpc) => context.APIRequest.PostAsync(origin + "/mcp", new()
                    {
                        Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret, ["Accept"] = "application/json, text/event-stream" },
                        DataObject = rpc,
                    });
                }
                Console.WriteLine("PASS: desktop/mobile profile, database CRUD, collections, versioned JSON merge patches, schema/SQL, keys/PATs, native/MCP state and revocation, paid organization checkout/project wizard and customer portal");
            }
            catch when (activePage is { IsClosed: false })
            {
                await activePage.ScreenshotAsync(new() { Path = Path.Combine(output, "failure.png"), FullPage = true, Mask = [activePage.Locator(".ts-apikeys-secret-value, #access-token-secret, input[type=password]")] });
                await File.WriteAllTextAsync(Path.Combine(output, "failure.html"), RedactCredentialEvidence(await activePage.ContentAsync()));
                Console.Error.WriteLine("Browser failed at " + RedactCredentialEvidence(activePage.Url));
                var body = await activePage.Locator("body").InnerTextAsync();
                var sanitized = RedactCredentialEvidence(body);
                Console.Error.WriteLine(sanitized[Math.Max(0, sanitized.Length - 2000)..]);
                throw;
            }
        }

        private static string RedactCredentialEvidence(string value)
        {
            return Regex.Replace(value,
            @"\b[0-9A-HJKMNP-TV-Z]{26}_[0-9a-hjkmnp-tv-z]{52}\b|\btspat_[A-Za-z0-9_-]+\b", "[REDACTED]");
        }

        [Test]
        public async Task SharedTextLinksAndStepsMeetWcagAcrossViewports()
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
            var page = await context.NewPageAsync();
            await BrowserAudit.SignInAsync(page, fixture.Origin);
            foreach (var width in new[] { 375, 768, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 900);
                foreach (var path in new[] { "/support", "/?tab=connect", "/auth/danger", "/missing-shared-accessibility" })
                {
                    await page.GotoAsync(new Uri(fixture.Origin, path).ToString());
                    await page.EvaluateAsync("() => document.fonts.ready");
                    await BrowserAudit.AssertAccessibleAsync(page, $"Shared link and step text on {path} at {width}px");
                }
            }
        }

        [Test]
        public async Task AccessibleSignInFailureAndNotFoundPages()
        {
            var output = BrowserAudit.Output(fixture.Root, "");
            var origin = fixture.Origin.ToString().TrimEnd('/');
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            foreach (var (name, width, height) in new[] { ("desktop", 1440, 1000), ("mobile", 390, 844) })
            {
                await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
                var page = await context.NewPageAsync();
                var errors = new ConcurrentQueue<string>();
                page.PageError += (_, error) => errors.Enqueue(error);
                // Sign-in UI lives on ts-auth's hosted pages; the dashboard only renders a failed callback.
                var response = await page.GotoAsync(origin + "/login/callback?state=forged&code=forged");
                await Assert.That(response!.Status).IsEqualTo(400);
                await page.GetByRole(AriaRole.Heading, new() { Name = "Sign in", Exact = true }).WaitForAsync();
                await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = "Try again", Exact = true }).GetAttributeAsync("href")).IsEqualTo("/login");
                await page.EvaluateAsync("() => document.fonts.ready");
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"signin-failure-{name}.png"), FullPage = true });
                await BrowserAudit.AssertAccessibleAsync(page, "WCAG 2.2 AA on the sign-in failure page " + name);
                await page.GotoAsync(origin + "/missing");
                await page.GetByRole(AriaRole.Heading, new() { Name = "We can’t find that page." }).WaitForAsync();
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"notfound-{name}.png"), FullPage = true });
                await BrowserAudit.AssertAccessibleAsync(page, "WCAG 2.2 AA on the not-found page " + name);
                await Assert.That(errors).IsEmpty().Because("No browser script errors");
            }
        }
    }
}
