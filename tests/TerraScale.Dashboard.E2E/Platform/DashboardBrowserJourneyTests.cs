using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
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
                var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
                var page = activePage = await context.NewPageAsync();
                var errors = new ConcurrentQueue<string>();
                page.PageError += (_, error) => errors.Enqueue(error);
                var name = $"qa-{size}-{Guid.NewGuid().ToString("N")[..10]}";
                await GotoAsync("/login");
                await page.Locator("#email").FillAsync("user@example.com");
                await page.Locator("#password").FillAsync("test-password");
                await SubmitAsync("form[action=\"/login\"]", "Sign in");
                await GotoAsync("/");
                var personalOrg = (await (await context.APIRequest.GetAsync(origin + "/api/v1/auth/me")).JsonAsync())!.Value.GetProperty("organizationId").GetString()!;
                await ShotAsync("home");
                await GotoAsync("/profile");
                await page.Locator("#displayName").FillAsync("QA Test User");
                await SubmitAsync("form[action=\"/api/v1/dashboard/profile\"]", "Save profile");
                await Assert.That(await page.Locator("#displayName").InputValueAsync()).IsEqualTo("QA Test User");
                await page.Locator("#displayName").FillAsync("Test User");
                await SubmitAsync("form[action=\"/api/v1/dashboard/profile\"]", "Save profile");

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
                var sfo = page.Locator("#database-region input[value=\"SFO\"]");
                var sfoInitially = await sfo.IsCheckedAsync();
                await map.GetByRole(AriaRole.Button, new() { Name = "San Francisco (SFO)", Exact = true }).ClickAsync();
                await Assert.That(await sfo.IsCheckedAsync()).IsEqualTo(!sfoInitially).Because("Map picks change the canonical native region checkbox");
                const string pressed = "expected => document.querySelector('#create-database button[data-region=\"SFO\"]').getAttribute('aria-pressed') === String(expected)";
                await page.WaitForFunctionAsync(pressed, !sfoInitially);
                await sfo.SetCheckedAsync(sfoInitially);
                await page.WaitForFunctionAsync(pressed, sfoInitially);
                await page.Locator("#create-database input[name=\"region\"][value=\"GRU\"]").CheckAsync();
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
                await page.Locator("#new-document-id").FillAsync("customer-1");
                await EditorAsync("#new-document-json", """{"name":"Ada","score":7,"nested":{"keep":true,"remove":false}}""");
                await SubmitAsync("#add-document form", "Add document");
                await page.GetByRole(AriaRole.Link, new() { Name = "customer-1", Exact = true }).ClickAsync();
                await page.Locator("#edit-document[open]").WaitForAsync();
                await page.Locator("#operation").SelectOptionAsync("patch");
                await EditorAsync("#document-json", """{"score":9,"nested":{"remove":null}}""");
                await SubmitAsync("#edit-document form", "Save document");
                await page.GetByRole(AriaRole.Link, new() { Name = "customer-1", Exact = true }).ClickAsync();
                await page.Locator("#edit-document[open]").WaitForAsync();
                var patched = JsonNode.Parse(await page.Locator("#document-json").InputValueAsync());
                await Assert.That(JsonNode.DeepEquals(patched, JsonNode.Parse("""{"name":"Ada","score":9,"nested":{"keep":true}}"""))).IsTrue()
                    .Because("Patch preserves unrelated fields and removes null members: " + patched?.ToJsonString());
                await page.Locator("#edit-document").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await ShotAsync("documents");
                await GotoAsync($"/databases/{databaseId}/schema?collection=users");
                await Assert.That(await page.Locator("#schema-json").InputValueAsync()).Contains("score").Because("Schema includes live fields");
                await GotoAsync($"/databases/{databaseId}/sql?collection=users");
                await EditorAsync("#sql", "SELECT * FROM users LIMIT 100");
                await SubmitAsync("form[action=\"/api/v1/dashboard/data/sql\"]", "Run query");
                await page.GetByText("Ada", new() { Exact = true }).WaitForAsync();
                await ShotAsync("sql");

                await GotoAsync("/api-keys?kind=key&create=key");
                await page.Locator("#create-key[open]").WaitForAsync();
                await page.Locator("#key-name").FillAsync(name);
                await page.Locator("#key-database").SelectOptionAsync(databaseId!);
                await page.Locator("#key-access-write").CheckAsync();
                await SubmitAsync("#create-key form", "Create key");
                var keySecret = page.Locator(".ts-apikeys-secret-value");
                await keySecret.WaitForAsync();
                var issuedKey = await keySecret.InnerTextAsync();
                await Assert.That(issuedKey).Matches("^tb_[A-Za-z0-9_-]+$").Because("The producer issues a usable native key");
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
                await page.Locator("#accessTokenPassword").FillAsync("test-password");
                await SubmitAsync("#profile-access-token-create form", "Create token");
                var secret = await page.Locator("#access-token-secret").InputValueAsync();
                await Assert.That(secret).Matches("^tspat_[A-Za-z0-9_-]{43}$");
                var mcp = await McpAsync(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "list_databases", arguments = new { } } });
                await Assert.That(mcp.Status).IsEqualTo(200);
                await Assert.That(await mcp.TextAsync()).Contains(name).Because("MCP observes the database created through native UI");
                await GotoAsync("/profile?tab=access-tokens");
                await Assert.That(await page.Locator("#access-token-secret").CountAsync()).IsEqualTo(0);
                var tokenRow = page.Locator("#profile-access-tokens tbody tr").Filter(new() { HasText = name });
                await tokenRow.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
                await page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Revoke token", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(new Regex(@"/profile\?tab=access-tokens"));
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
                    await page.Locator("#create-organization select[name=\"regions\"]").SelectOptionAsync(new[] { "GRU", "SFO" });
                    await page.Locator("#create-organization select[name=\"region\"]").SelectOptionAsync("GRU");
                    await page.Locator("#create-organization select[name=\"backupRegion\"]").SelectOptionAsync("SFO");
                    await SubmitAsync("#create-organization form", "Create project");
                    await GotoAsync("/organization?tab=billing");
                    await page.GetByText("Starter", new() { Exact = true }).First.WaitForAsync();
                    await Assert.That(await page.Locator("#billing-summary").InnerTextAsync()).Contains("On file").Because("Stripe confirmation activates the bound organization");
                    await Assert.That(await page.Locator("form[action=\"/billing/checkout\"]").CountAsync()).IsEqualTo(0).Because("Active paid subscriptions use the customer portal");
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
                Task ShotAsync(string key) => page.ScreenshotAsync(new() { Path = Path.Combine(output, $"{key}-{size}.png"), FullPage = true, Caret = ScreenshotCaret.Hide });
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
            await activePage.ScreenshotAsync(new() { Path = Path.Combine(output, "failure.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(output, "failure.html"), await activePage.ContentAsync());
            Console.Error.WriteLine("Browser failed at " + activePage.Url);
            var body = await activePage.Locator("body").InnerTextAsync();
            Console.Error.WriteLine(body[Math.Max(0, body.Length - 2000)..]);
            throw;
        }
    }

    [Test]
    public async Task AccessibleAuthPagesAndNativePasswordControls()
    {
        var output = BrowserAudit.Output(fixture.Root, "");
        var origin = fixture.Origin.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        foreach (var (name, width, height) in new[] { ("desktop", 1440, 1000), ("mobile", 390, 844) })
        {
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
            var page = await context.NewPageAsync();
            var errors = new ConcurrentQueue<string>();
            page.PageError += (_, error) => errors.Enqueue(error);
            var response = await page.GotoAsync(origin + "/login");
            await Assert.That(response!.Status).IsEqualTo(200);
            var password = page.Locator("#password");
            await password.FillAsync("browser-test-password");
            await page.GetByRole(AriaRole.Button, new() { Name = "Show password" }).ClickAsync();
            await Assert.That(await password.GetAttributeAsync("type")).IsEqualTo("text");
            await page.GetByRole(AriaRole.Button, new() { Name = "Hide password" }).ClickAsync();
            await Assert.That(await password.GetAttributeAsync("type")).IsEqualTo("password");
            await password.FillAsync("");
            await page.EvaluateAsync("() => document.fonts.ready");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"login-{name}.png"), FullPage = true });
            await BrowserAudit.AssertAccessibleAsync(page, "WCAG 2.2 AA on /login " + name);
            await page.GetByRole(AriaRole.Link, new() { Name = "Forgot password" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Reset password" }).WaitForAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"reset-{name}.png"), FullPage = true });
            await page.GotoAsync(origin + "/login/code");
            await page.GetByRole(AriaRole.Heading, new() { Name = "Sign in with a code" }).WaitForAsync();
            await page.GotoAsync(origin + "/missing");
            await page.GetByRole(AriaRole.Heading, new() { Name = "We can’t find that page." }).WaitForAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"notfound-{name}.png"), FullPage = true });
            await Assert.That(errors).IsEmpty().Because("No browser script errors");
        }
    }
}
