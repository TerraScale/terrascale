using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class HttpsTransportJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task IsolatedTlsRejectsUntrustedClientsAndChromiumEnforcesHostCookieRules()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        await using var app = environment.CreateTestHost(fixture.Root, 0, "https://127.0.0.1");
        app.MapGet("/__test/host-cookie", (HttpContext http) =>
        {
            var domain = http.Request.Query.ContainsKey("domain");
            http.Response.Cookies.Append(domain ? "__Host-invalid" : "__Host-valid", "synthetic", new()
            { Secure = true, HttpOnly = true, Path = "/", Domain = domain ? "q20.test" : null });
            http.Response.Cookies.Append("domain-control", "synthetic", new() { Secure = true, Path = "/", Domain = "q20.test" });
            return Results.Text("ok");
        });
        await app.StartAsync();
        var origin = new Uri(app.Urls.Single());
        await Assert.That(origin.Scheme).IsEqualTo("https");
        using (var untrusted = new HttpClient())
        {
            var refused = false;
            try { using var response = await untrusted.GetAsync(new Uri(origin, "/healthz")); }
            catch (HttpRequestException) { refused = true; }
            await Assert.That(refused).IsTrue();
        }
        using (var trusted = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate }))
        using (var response = await trusted.GetAsync(new Uri(origin, "/healthz")))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Args = ["--host-resolver-rules=MAP *.q20.test 127.0.0.1", "--no-proxy-server"] });
        // Only this isolated browser context accepts its test certificate; browser cookie rules remain active.
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var dashboard = new UriBuilder(origin) { Host = "dash.q20.test", Path = "/__test/host-cookie" }.Uri;
        var sibling = new UriBuilder(dashboard) { Host = "sibling.q20.test", Query = "domain=1" }.Uri;
        await page.GotoAsync(dashboard.ToString());
        await page.GotoAsync(sibling.ToString());
        var cookies = await context.CookiesAsync();
        await Assert.That(cookies.Any(cookie => cookie.Name == "__Host-valid" && cookie.Secure && cookie.HttpOnly && cookie.Path == "/")).IsTrue();
        await Assert.That(cookies.Any(cookie => cookie.Name == "domain-control")).IsTrue();
        await Assert.That(cookies.Any(cookie => cookie.Name == "__Host-invalid")).IsFalse();
        var plainBuilder = WebApplication.CreateBuilder();
        plainBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var plain = plainBuilder.Build();
        plain.MapGet("/", (HttpContext http) =>
        {
            http.Response.Cookies.Append("__Host-http", "synthetic", new() { Secure = true, HttpOnly = true, Path = "/" });
            return Results.Text("ok");
        });
        await plain.StartAsync();
        await page.GotoAsync(new UriBuilder(plain.Urls.Single()) { Host = "dash.q20.test" }.Uri.ToString());
        await Assert.That((await context.CookiesAsync()).Any(cookie => cookie.Name == "__Host-http")).IsFalse();
        await plain.StopAsync();
        await app.StopAsync();
    }
}
