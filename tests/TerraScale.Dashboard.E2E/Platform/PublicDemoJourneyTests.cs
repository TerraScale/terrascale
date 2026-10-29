using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using TerraScale.Dashboard.Features.AuthenticationManagement;

namespace TerraScale.Dashboard.E2E.Platform;

// The public synthetic demo on dash.terrascale.tech (DEMO=true): the demo host plus PublicDemo.
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
    public async Task FullDocumentsCarryNoDemoBanner()
    {
        await using var demo = await StartAsync();
        using var client = demo.Client();
        using (var login = await HttpJourney.LoginAsync(client))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var path in new[] { "/", "/databases", "/profile", "/organization" })
            await AssertUnlabelledAsync(client, path);
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
            (HttpMethod.Post, "/mcp"),
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
    public async Task AuthenticationConsoleRendersEverySectionFromTheSyntheticTenant()
    {
        await using var demo = await StartAsync();
        using var client = demo.Client();
        using (var anonymous = await client.GetAsync("/auth/users"))
        {
            await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(anonymous.Headers.Location?.OriginalString).IsEqualTo("/login?returnTo=%2Fauth%2Fusers").Because("Signed-out visitors still sign in first.");
        }
        using (var login = await HttpJourney.LoginAsync(client))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var section in AuthenticationSection.All)
        {
            using var response = await client.GetAsync("/auth/" + section.Key);
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(section.Key);
            await Assert.That(html).Contains($"data-auth-section=\"{section.Key}\"");
            await Assert.That(html).DoesNotContain("id=\"auth-error\"").Because($"{section.Key} shows the synthetic tenant, not an error.");
            if (section.Key is "users" or "sessions" or "clients")
                await Assert.That(Regex.IsMatch(html, @"<tbody>\s*<tr>")).IsTrue().Because($"{section.Key} lists synthetic rows.");
        }
        var users = await client.GetStringAsync("/auth/users?q=globex");
        await Assert.That(users).Contains("alan@globex.io").Because("Search filters the rows.");
        await Assert.That(users).DoesNotContain("ada@northwind.app");
        var signIn = await client.GetStringAsync("/auth/sign-in");
        await Assert.That(signIn).Contains("role=\"switch\" id=\"auth-method-password\" name=\"password\" checked");
        foreach (var path in new[] { "/api/v1/auth-management/clients", "/api/v1/auth-management/settings", "/api/v1/auth-management/branding" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "x" }) };
            request.Headers.Add("Origin", demo.Origin.GetLeftPart(UriPartial.Authority));
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because($"POST {path} stays blocked in the public demo.");
        }
    }

    private static async Task AssertUnlabelledAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(path);
        await Assert.That(html).DoesNotContain("DEMO").Because($"{path} shows no DEMO banner or title.");
    }

    private static async Task<PublicDemoHost> StartAsync()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "TerraScale.slnx"))) root = Path.GetDirectoryName(root)!;
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        var app = environment.CreateTestHost(root, 0, "https://127.0.0.1", PublicDemo.Configure);
        app.MapGet("/__demo-error-probe", string () => throw new InvalidOperationException(Secret));
        await app.StartAsync();
        return new(app, new(app.Urls.Single()));
    }

    private sealed record PublicDemoHost(WebApplication App, Uri Origin) : IAsyncDisposable
    {
        public HttpClient Client() => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(30) };

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
