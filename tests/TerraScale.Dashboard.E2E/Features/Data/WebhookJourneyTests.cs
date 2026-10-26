using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    [Arguments("timeout")]
    [Arguments("malformed")]
    [Arguments("io")]
    public async Task PersistedCreateWithLostResponseRequiresReconciliationBeforeRetry(string fault)
    {
        await using var flow = await Flow.StartAsync(fixture.Root);
        flow.CreateFault = fault;
        using var response = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/webhooks", flow.Form());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("The result is unknown. Check the endpoint list before retrying; delete and recreate it if its signing secret was lost.");
        await Assert.That(body).Contains("href=\"" + flow.Path.Replace("&", "&amp;", StringComparison.Ordinal) + "\"");
        await Assert.That(body).DoesNotContain("Try again shortly");
        await Assert.That(body).DoesNotContain(Flow.Secret);
        await Assert.That(flow.Created).IsTrue();
        await Assert.That(flow.Calls).IsEqualTo(1).Because("A failed acknowledgment must never replay creation.");
        var listed = await flow.Client.GetStringAsync(flow.Path);
        await Assert.That(listed).Contains("https://receiver.example/events");
        await Assert.That(listed).DoesNotContain(Flow.Secret);
    }

    [Test]
    public async Task CallerCancellationIsNotConvertedToAnUnknownCreateResponse()
    {
        await using var flow = await Flow.StartAsync(fixture.Root);
        flow.CreateFault = "cancel";
        using var cancellation = new CancellationTokenSource();
        var request = flow.Client.PostAsync("/api/v1/dashboard/webhooks", new FormUrlEncodedContent(flow.Form()), cancellation.Token);
        await flow.CreatePersisted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await request);
        await Assert.That(flow.Created).IsTrue();
        await Assert.That(flow.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ProducerValidationRefusalKeepsItsKnownOutcome()
    {
        await using var flow = await Flow.StartAsync(fixture.Root);
        flow.CreateFault = "refused";
        using var response = await HttpJourney.PostFormAsync(flow.Client, "/api/v1/dashboard/webhooks", flow.Form());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("The result is unknown");
        await Assert.That(flow.Created).IsFalse();
        await Assert.That(flow.Calls).IsEqualTo(1);
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
        var output = BrowserAudit.Output(fixture.Root, "webhooks-" + width + "x" + height);
        await using var context = await browser.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new() { Width = width, Height = height },
            RecordVideoDir = Path.Combine(output, "video"),
            RecordVideoSize = new() { Width = width, Height = height }
        });
        // Mask capture text through the allowed stylesheet; do not bypass the application's CSP.
        await context.RouteAsync("**/css/terrascale.css*", async route =>
        {
            var response = await route.FetchAsync();
            await route.FulfillAsync(new()
            {
                Response = response,
                Body = await response.TextAsync() + "\n.ts-account-name,.ts-account-email {visibility:hidden!important} #webhook-secret {color:transparent!important;position:relative} #webhook-secret::before {content:'[signing secret masked]';color:#fff;position:absolute}"
            });
        });
        var page = await context.NewPageAsync();
        await BrowserAudit.SignInAsync(page, flow.Scenario.Origin, email: flow.Account.Email, password: flow.Account.Password);
        await page.GotoAsync(new Uri(flow.Scenario.Origin, "/databases/" + flow.DatabaseId + "/documents/items").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Webhooks", Exact = true }).ClickAsync();
        await Assert.That(await page.Locator("#webhook-collection").InputValueAsync()).IsEqualTo("items");
        await Assert.That(await page.GetByText("No webhooks yet.", new() { Exact = true }).IsVisibleAsync()).IsTrue();
        await ShotAsync("1-empty");
        flow.CreateFault = "malformed";
        await page.GetByLabel("HTTPS endpoint", new() { Exact = true }).FillAsync("https://receiver.example/events");
        var unknown = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Create webhook", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/dashboard/webhooks");
        await Assert.That(unknown.Status).IsEqualTo(503);
        await page.GetByText("The result is unknown. Check the endpoint list before retrying; delete and recreate it if its signing secret was lost.", new() { Exact = true }).WaitForAsync();
        await ShotAsync("2-unknown-create");
        await page.GetByRole(AriaRole.Link, new() { Name = "Reload webhooks", Exact = true }).ClickAsync();
        await page.Locator("#webhooks").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete webhook", Exact = true }).First.ClickAsync();
        await page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Delete webhook", Exact = true }).ClickAsync();
        await page.GetByText("No webhooks yet.", new() { Exact = true }).WaitForAsync();
        flow.CreateFault = "";
        await page.GetByLabel("HTTPS endpoint", new() { Exact = true }).FillAsync("https://receiver.example/events");
        var created = await page.RunAndWaitForResponseAsync(() => page.Locator("form[action='/api/v1/dashboard/webhooks']")
            .EvaluateAsync("form => { form.requestSubmit(); form.requestSubmit(); }"),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/dashboard/webhooks");
        await Assert.That(created.Status).IsEqualTo(201);
        await Assert.That(flow.Creates).IsEqualTo(2).Because("The failed acknowledgement and later successful create each send one mutation despite repeated submission.");
        await page.Locator("#webhook-secret").WaitForAsync();
        await Assert.That(await page.Locator("#webhook-secret").EvaluateAsync<string>("el => getComputedStyle(el).color")).IsEqualTo("rgba(0, 0, 0, 0)");
        await Assert.That(await page.Locator(".ts-account-email").First.EvaluateAsync<string>("el => getComputedStyle(el).visibility")).IsEqualTo("hidden");
        await Assert.That(await page.Locator("#webhook-secret").InnerTextAsync()).IsEqualTo(Flow.Secret);
        await Assert.That(await page.EvaluateAsync<bool>("secret => !JSON.stringify([localStorage, sessionStorage]).includes(secret)", Flow.Secret)).IsTrue();
        await ShotAsync("3-created-secret-masked");
        await page.GetByRole(AriaRole.Link, new() { Name = "Continue to webhooks", Exact = true }).ClickAsync();
        await page.Locator("#webhooks").WaitForAsync();
        await Assert.That((await page.ContentAsync()).Contains(Flow.Secret, StringComparison.Ordinal)).IsFalse();
        await flow.WriteAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Delivery log", Exact = true }).ClickAsync();
        await page.Locator("#webhook-deliveries").WaitForAsync();
        await Assert.That(await page.Locator("#webhook-deliveries tbody").InnerTextAsync()).Contains("delivered");
        await page.WaitForLoadStateAsync(LoadState.Load);
        await BrowserAudit.AssertAccessibleAsync(page, "Webhook controls and delivery records remain accessible.");
        await ShotAsync("4-delivery");
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete webhook", Exact = true }).First.ClickAsync();
        await Assert.That(await page.Locator("dialog[open]").InnerTextAsync()).Contains("https://receiver.example/events");
        await ShotAsync("4a-named-delete-confirmation");
        await page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assert.That(flow.Created).IsTrue();
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete webhook", Exact = true }).First.ClickAsync();
        await page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Delete webhook", Exact = true }).ClickAsync();
        await page.GetByText("No webhooks yet.", new() { Exact = true }).WaitForAsync();
        await Assert.That(flow.Created).IsFalse();
        await ShotAsync("5-deleted");
        await context.CloseAsync();
        await page.Video!.SaveAsAsync(Path.Combine(output, "synthetic-webhook-flow.webm"));

        async Task ShotAsync(string state) => await page.ScreenshotAsync(new()
        {
            Path = Path.Combine(output, state + ".png"),
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled,
            Mask = [page.Locator("#webhook-secret"), page.Locator(".ts-account-email"), page.Locator(".ts-account-name")]
        });
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
        public int Creates { get; private set; }
        public string CreateFault { get; set; } = "";
        public TaskCompletionSource CreatePersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string csrf = "", organization = "", project = "";
        private TestAuth auth = null!;
        private bool delivered;

        public static async Task<Flow> StartAsync(string root)
        {
            var flow = new Flow();
            flow.Scenario = await DatabaseSdkScenario.StartAsync(root, builder =>
            {
                DatabaseSdkJourneyTests.CaptureAuth(value => flow.auth = value)(builder);
                var inner = (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new TimeoutFactory(inner, flow)));
            }, flow.DispatchAsync);
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
                Creates++;
                using var body = await JsonDocument.ParseAsync(http.Request.Body);
                if (body.RootElement.GetProperty("url").GetString() != "https://receiver.example/events") throw new InvalidOperationException("Unexpected endpoint.");
                if (CreateFault == "refused") return Results.Json(new { error = new { code = "invalid_request", message = "Invalid endpoint." } }, statusCode: 400);
                Created = true;
                CreatePersisted.TrySetResult();
                if (CreateFault is "timeout" or "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted);
                if (CreateFault == "malformed") return Results.Content("{", "application/json", statusCode: 201);
                if (CreateFault == "io")
                {
                    http.Response.StatusCode = 201;
                    http.Response.ContentLength = 1000;
                    await http.Response.WriteAsync("{", http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                    http.Abort();
                    return Results.Empty;
                }
                return Results.Json(new { id = HookId, url = "https://receiver.example/events", createdAt = DateTimeOffset.UtcNow, status = "active", secret = Secret }, statusCode: 201);
            }
            if (http.Request.Method == "DELETE") { Created = false; return Results.NoContent(); }
            if (http.Request.Query["limit"] != "100") throw new InvalidOperationException("Read all bounded retained webhook records.");
            if (path.EndsWith("/deliveries", StringComparison.Ordinal))
                return Results.Json(delivered ? new[] { new { id = "01K78D0C000000000000000002", createdAt = DateTimeOffset.UtcNow, status = "delivered", attempts = 1, lastAttemptAt = (DateTimeOffset?)DateTimeOffset.UtcNow, nextAttemptAt = (DateTimeOffset?)null, httpStatus = (int?)204, errorCode = (string?)null } } : []);
            return Results.Json(Created ? new[] { new { id = HookId, url = "https://receiver.example/events", createdAt = DateTimeOffset.UtcNow, status = FeedGap ? "feed_gap" : "active" } } : []);
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await Scenario.DisposeAsync(); }

        private sealed class TimeoutFactory(IHttpClientFactory inner, Flow flow) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                var client = inner.CreateClient(name);
                if (flow.CreateFault == "timeout") client.Timeout = TimeSpan.FromSeconds(3);
                return client;
            }
        }
    }
}
