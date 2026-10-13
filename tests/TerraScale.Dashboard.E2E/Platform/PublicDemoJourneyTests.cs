using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

// The public synthetic demo on dash.terrascale.tech (deploy/demo): the demo host plus PublicDemo.
[NotInParallel("dashboard-demo")]
public sealed class PublicDemoJourneyTests
{
    private const string Secret = "public-demo-secret-detail";

    [Test]
    public async Task UnhandledErrorsReturnGenericResponsesWithoutStackTraces()
    {
        await using var demo = await StartAsync();
        using var client = demo.Client();
        using var response = await client.GetAsync("/__demo-error-probe");
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(body).DoesNotContain(Secret);
        await Assert.That(body).DoesNotContain(nameof(InvalidOperationException));
        await Assert.That(body).DoesNotContain(" at ").Because("No stack frame reaches a public response.");
        using var dev = await client.GetAsync("/__dev/events");
        await Assert.That(dev.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because("Development-only endpoints stay unmapped.");
    }

    [Test]
    public async Task EveryFullDocumentCarriesOneDemoBanner()
    {
        await using var demo = await StartAsync();
        using var client = demo.Client();
        await AssertLabelledAsync(client, "/login");
        using (var login = await HttpJourney.LoginAsync(client))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var path in new[] { "/", "/databases", "/profile", "/organization" })
            await AssertLabelledAsync(client, path);
    }

    [Test]
    public async Task AdminMcpScenarioAndDestructiveRoutesAreBlockedEvenWhenSignedIn()
    {
        await using var demo = await StartAsync();
        using var client = demo.Client();
        using (var login = await HttpJourney.LoginAsync(client))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var blocked = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/admin"), (HttpMethod.Get, "/admin/users"), (HttpMethod.Post, "/api/v1/admin/plans"),
            (HttpMethod.Post, "/api/v1/admin/plans/p/delete"), (HttpMethod.Post, "/api/v1/auth-management/clients"),
            (HttpMethod.Post, "/mcp"), (HttpMethod.Get, "/reset-password"), (HttpMethod.Post, "/reset-password/complete"),
            (HttpMethod.Post, "/api/v1/dashboard/organization/projects/p/delete"), (HttpMethod.Post, "/api/v1/dashboard/organization/leave"),
            (HttpMethod.Post, "/api/v1/dashboard/credentials/revoke"), (HttpMethod.Post, "/api/v1/dashboard/profile/access-tokens/t/revoke"),
            (HttpMethod.Post, "/api/v1/access/members/m/revoke"), (HttpMethod.Delete, "/api/v1/dashboard/databases/d"),
            (HttpMethod.Post, "/__test/billing/webhook/event"), (HttpMethod.Post, "/__test/reporting/scenario"), (HttpMethod.Get, "/__test/session"),
        };
        foreach (var (method, path) in blocked)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Origin", demo.Origin.GetLeftPart(UriPartial.Authority));
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because($"{method} {path} is disabled in the public demo.");
            await Assert.That(await response.Content.ReadAsStringAsync()).StartsWith("DEMO:");
        }
        using var portal = await client.GetAsync("/__test/portal");
        await Assert.That(portal.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("The fake Stripe portal stays reachable for the billing walkthrough.");
    }

    [Test]
    [Category("Slow")]
    public async Task BannerPersistsThroughInPageNavigationAndDatastarUpdates()
    {
        await using var demo = await StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var banner = page.GetByRole(AriaRole.Status).Filter(new() { HasText = PublicDemo.Banner });
        await page.GotoAsync(demo.Origin + "login");
        await Assertions.Expect(banner).ToHaveCountAsync(1);
        await page.Locator("#email").FillAsync("user@example.com");
        await page.Locator("#password").FillAsync("test-password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(demo.Origin.ToString());
        await Assertions.Expect(banner).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Link, new() { Name = "Databases", Exact = true }).First.ClickAsync();
        await page.WaitForURLAsync(demo.Origin + "databases");
        await Assertions.Expect(banner).ToBeVisibleAsync();
        // Datastar patches the open dialog in place; the banner must survive the morph.
        await page.GetByRole(AriaRole.Button, new() { Name = "New database", Exact = true }).First.ClickAsync();
        await page.Locator("#create-database[open]").WaitForAsync();
        await Assertions.Expect(banner).ToHaveCountAsync(1);
        await page.Keyboard.PressAsync("Escape");
        await page.Locator("#create-database[open]").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.GetByRole(AriaRole.Link, new() { Name = "Usage", Exact = true }).First.ClickAsync();
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/usage"));
        await Assertions.Expect(banner).ToHaveCountAsync(1);
        await Assert.That(await page.TitleAsync()).StartsWith("DEMO · ");
    }

    private static async Task AssertLabelledAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(path);
        await Assert.That(html.Split(PublicDemo.Banner).Length - 1).IsEqualTo(1).Because($"{path} shows the DEMO banner once.");
        await Assert.That(html).Contains("<title>DEMO · ");
        await Assert.That(html).Contains("href=\"/__demo/banner.css\"");
    }

    private static async Task<PublicDemoHost> StartAsync()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "TerraScale.Dashboard.slnx"))) root = Path.GetDirectoryName(root)!;
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var origin = new Uri($"http://127.0.0.1:{port}/");
        var app = environment.CreateHost(root, port, origin.ToString().TrimEnd('/'), PublicDemo.Configure);
        PublicDemo.Use(app);
        app.MapGet("/__demo-error-probe", string () => throw new InvalidOperationException(Secret));
        await app.StartAsync();
        return new(app, origin);
    }

    private sealed record PublicDemoHost(WebApplication App, Uri Origin) : IAsyncDisposable
    {
        public HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(30) };

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
