using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
public sealed class WebhookJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task WebhooksUseScopedSdkAndProtectSecretsCsrfAndResourceAccess()
    {
        await using var flow = await Flow.StartAsync(fixture.Root);
        using var anonymous = flow.Scenario.CreateClient();
        using (var refused = await HttpJourney.PostFormAsync(anonymous, "/api/v1/dashboard/webhooks", flow.Form()))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var missingCsrf = flow.Form(); missingCsrf.Remove("_csrf");
        using (var refused = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/webhooks", missingCsrf))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var foreign = flow.Form(); foreign["databaseId"] = "01K78D0C000000000000000099";
        using (var refused = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/webhooks", foreign))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var invalidUrl = flow.Form(); invalidUrl["url"] = "http://receiver.example/events";
        using (var refused = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/webhooks", invalidUrl))
        {
            await Assert.That((int)refused.StatusCode).IsEqualTo(422);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("HTTPS URL on port 443");
        }
        await Assert.That(flow.Calls).IsEqualTo(0);
        using (var created = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/webhooks", flow.Form()))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(created.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(created.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("no-referrer");
            await Assert.That(await created.Content.ReadAsStringAsync()).Contains(Flow.Secret);
        }
        // Creation has no follow-up API calls that could discard the one-time response.
        await Assert.That(flow.Calls).IsEqualTo(1);
        using var listResponse = await flow.Client.GetAsync(flow.Path);
        await Assert.That(listResponse.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("same-origin");
        var listed = await listResponse.Content.ReadAsStringAsync();
        await Assert.That(listed.Contains(Flow.Secret, StringComparison.Ordinal)).IsFalse();
        await Assert.That(listed).Contains("https://receiver.example/events");
        await flow.WriteAsync();
        var log = await flow.Client.GetStringAsync(flow.Path + "&webhook=" + Flow.HookId);
        await Assert.That(log).Contains("delivered");
        await Assert.That(log).Contains("id=\"webhook-deliveries\"");
        flow.FeedGap = true;
        await Assert.That(await flow.Client.GetStringAsync(flow.Path)).Contains("Reconcile current data");
        using (var controls = flow.Scenario.CreateProviderClient())
        using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "viewer" })) role.EnsureSuccessStatusCode();
        using (var refused = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/webhooks/delete", flow.Form()))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    [Category("Slow")]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task BrowserCreatesWebhookViewsDeliveryAndDeletesWithoutPersistingSecret(int width, int height)
    {
        await using var flow = await Flow.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await BrowserAudit.SignInAsync(page, flow.Scenario.Origin, email: flow.Account.Email, password: flow.Account.Password);
        await page.GotoAsync(new Uri(flow.Scenario.Origin, "/databases/" + flow.DatabaseId + "/documents/items").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Webhooks", Exact = true }).ClickAsync();
        await Assert.That(await page.Locator("#webhook-collection").InputValueAsync()).IsEqualTo("items");
        await Assert.That(await page.GetByText("No webhooks yet.", new() { Exact = true }).IsVisibleAsync()).IsTrue();
        await page.GetByLabel("HTTPS endpoint", new() { Exact = true }).FillAsync("https://receiver.example/events");
        var created = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Create webhook", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/dashboard/webhooks");
        await Assert.That(created.Status).IsEqualTo(201);
        await page.Locator("#webhook-secret").WaitForAsync();
        await Assert.That(await page.Locator("#webhook-secret").InnerTextAsync()).IsEqualTo(Flow.Secret);
        await Assert.That(await page.EvaluateAsync<bool>("secret => !JSON.stringify([localStorage, sessionStorage]).includes(secret)", Flow.Secret)).IsTrue();
        await page.GetByRole(AriaRole.Link, new() { Name = "Continue to webhooks", Exact = true }).ClickAsync();
        await page.Locator("#webhooks").WaitForAsync();
        await Assert.That((await page.ContentAsync()).Contains(Flow.Secret, StringComparison.Ordinal)).IsFalse();
        await flow.WriteAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Delivery log", Exact = true }).ClickAsync();
        await page.Locator("#webhook-deliveries").WaitForAsync();
        await Assert.That(await page.Locator("#webhook-deliveries tbody").InnerTextAsync()).Contains("delivered");
        await BrowserAudit.AssertAccessibleAsync(page, "Webhook controls and delivery records remain accessible.");
        await page.GetByText("Delete webhook", new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm delete webhook", Exact = true }).ClickAsync();
        await page.GetByText("No webhooks yet.", new() { Exact = true }).WaitForAsync();
        await Assert.That(flow.Created).IsFalse();
    }

    // External HTTP contract fixture: no TerraBase process or receiver is claimed here.
    private sealed class Flow : IAsyncDisposable
    {
        public const string HookId = "01K78D0C000000000000000001";
        public const string Secret = "synthetic-signing-key-for-http-fixture-only";
        public DatabaseSdkScenario Scenario { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public (string Email, string Password) Account { get; private set; }
        public string DatabaseId { get; private set; } = "";
        public string Path => "/databases/" + DatabaseId + "/webhooks?collection=items";
        public bool Created { get; private set; }
        public bool FeedGap { get; set; }
        public int Calls { get; private set; }
        private string csrf = "", organization = "", project = "";
        private TestAuth auth = null!;
        private bool delivered;

        public static async Task<Flow> StartAsync(string root)
        {
            var flow = new Flow();
            flow.Scenario = await DatabaseSdkScenario.StartAsync(root, DatabaseSdkJourneyTests.CaptureAuth(value => flow.auth = value), flow.DispatchAsync);
            flow.Client = flow.Scenario.CreateClient();
            flow.Account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(flow.Client);
            using var identity = JsonDocument.Parse(await flow.Client.GetStringAsync("/api/v1/auth/me"));
            flow.organization = identity.RootElement.GetProperty("organizationId").GetString()!;
            flow.project = identity.RootElement.GetProperty("projectId").GetString()!;
            flow.csrf = await HttpJourney.CsrfAsync(flow.Client, "/databases");
            using var database = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = flow.csrf, ["name"] = "webhook-journey", ["region"] = "GRU" });
            database.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await database.Content.ReadAsStringAsync());
            flow.DatabaseId = body.RootElement.GetProperty("id").GetString()!;
            using var collection = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = flow.csrf, ["databaseId"] = flow.DatabaseId, ["name"] = "items" });
            collection.EnsureSuccessStatusCode();
            return flow;
        }

        public Dictionary<string, string> Form() => new()
        { ["_csrf"] = csrf, ["databaseId"] = DatabaseId, ["collection"] = "items", ["url"] = "https://receiver.example/events", ["id"] = HookId };

        public async Task WriteAsync()
        {
            using var response = await HttpJourney.PostFormAsync(Client, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = DatabaseId, ["collection"] = "items", ["operation"] = "create", ["id"] = "01K78D0C000000000000000003", ["document"] = "{}" });
            response.EnsureSuccessStatusCode();
        }

        private async Task<IResult?> DispatchAsync(HttpContext http)
        {
            var path = http.Request.Path.Value!;
            if (Created && http.Request.Method == "PUT" && path.EndsWith("/documents/by-key", StringComparison.Ordinal)) delivered = true;
            if (!path.Contains("/webhooks", StringComparison.Ordinal)) return null;
            Calls++;
            var bearer = http.Request.Headers.Authorization.ToString();
            var grant = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? auth.ResolveProjectToken(bearer[7..]) : null;
            var scopes = http.Request.Method == "GET" ? new[] { "documents:read" } : ["documents:read", "documents:write"];
            if (grant is not { } current || current.OrganizationId != organization || current.ProjectId != project || !current.Scopes.Order(StringComparer.Ordinal).SequenceEqual(scopes))
                throw new InvalidOperationException("Webhook SDK requests require exactly the existing document scopes for the current project.");
            var basePath = "/v1/projects/" + project + "/databases/webhook-journey/collections/items/webhooks";
            if (!path.StartsWith(basePath, StringComparison.Ordinal) || http.Request.Host.Host != "gru.api.terrascale.tech")
                throw new InvalidOperationException("Webhook SDK path must bind the selected collection and home region.");
            http.Response.Headers["X-TerraBase-Region"] = "gru";
            if (http.Request.Method == "POST")
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body);
                if (body.RootElement.GetProperty("url").GetString() != "https://receiver.example/events") throw new InvalidOperationException("Unexpected endpoint.");
                Created = true;
                return Results.Json(new { id = HookId, url = "https://receiver.example/events", createdAt = DateTimeOffset.UtcNow, status = "active", secret = Secret }, statusCode: 201);
            }
            if (http.Request.Method == "DELETE") { Created = false; return Results.NoContent(); }
            if (http.Request.Query["limit"] != "100") throw new InvalidOperationException("Read all bounded retained webhook records.");
            if (path.EndsWith("/deliveries", StringComparison.Ordinal))
                return Results.Json(delivered ? new[] { new { id = "01K78D0C000000000000000002", createdAt = DateTimeOffset.UtcNow, status = "delivered", attempts = 1, lastAttemptAt = (DateTimeOffset?)DateTimeOffset.UtcNow, nextAttemptAt = (DateTimeOffset?)null, httpStatus = (int?)204, errorCode = (string?)null } } : []);
            return Results.Json(Created ? new[] { new { id = HookId, url = "https://receiver.example/events", createdAt = DateTimeOffset.UtcNow, status = FeedGap ? "feed_gap" : "active" } } : []);
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await Scenario.DisposeAsync(); }
    }
}
