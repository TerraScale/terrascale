using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Platform;

public sealed partial class OpenApiTests
{
    private static readonly string[] ExpectedSchemes = ["ManagementBearer", "OAuthAccessBearer", "OperatorBearer", "SessionCookie", "bearerAuth"];
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    [Test]
    public async Task The_contract_is_served_and_complete()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
        request.Headers.Host = "attacker.example";
        using var response = await server.Client.SendAsync(request);
        var document = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var paths = document.GetProperty("paths");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(document.GetProperty("openapi").GetString()).StartsWith("3.");
        await Assert.That(document.GetProperty("info").GetProperty("title").GetString()).IsEqualTo("TerraScale Auth API");
        await Assert.That(paths.TryGetProperty("/healthz", out _)).IsTrue();
        await Assert.That(paths.TryGetProperty("/readyz", out _)).IsTrue();
        await Assert.That(paths.TryGetProperty("/admin/{asset}", out _)).IsTrue();
        // /metrics was replaced by OTLP push; it must not come back unannounced.
        await Assert.That(paths.TryGetProperty("/metrics", out _)).IsFalse();

        // Probes and bundled assets are public and say so explicitly.
        var healthz = paths.GetProperty("/healthz").GetProperty("get");
        await Assert.That(healthz.GetProperty("security").EnumerateArray().Single().EnumerateObject().Any()).IsFalse();
        // The server is the configured public origin, not whatever Host header the request sent.
        await Assert.That(document.GetProperty("servers")[0].GetProperty("url").GetString()).IsEqualTo("https://auth.test.invalid");

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in Methods)
            {
                if (!path.Value.TryGetProperty(method, out var operation))
                {
                    continue;
                }

                await Assert.That(operation.GetProperty("responses").EnumerateObject().Any()).IsTrue();
                if (operation.TryGetProperty("operationId", out var id))
                {
                    await Assert.That(operationIds.Add(id.GetString()!)).IsTrue();
                }
            }
        }
    }

    [Test]
    public async Task Operations_declare_the_credential_their_route_requires()
    {
        // Test-only routes stand in for the feature endpoints that later streams add. They
        // carry a described tag: an undescribed one fails document generation by design.
        await using var server = await AuthTestServer.StartAsync(routes: app =>
        {
            app.MapGet("/v1/{slug}/__test/users", () => Results.Ok()).WithName("test_management").WithTags("Admin");
            app.MapGet("/ui/{slug}/admin/__test", () => Results.Ok()).WithName("test_admin").WithTags("Admin");
            app.MapGet("/{slug}/oauth/userinfo", () => Results.Ok()).WithName("test_userinfo").WithTags("Admin");
            app.MapPost("/v1/instances", () => Results.Ok()).WithName("test_instances").WithTags("Admin");
            app.MapGet("/v1/{slug}/__test/public", () => Results.Ok()).WithName("test_public").WithTags("Admin").AllowAnonymous();
            app.MapPost("/scim/{directory}/__test", () => Results.Ok()).WithName("test_scim").WithTags("Admin")
                .WithMetadata(new TerraScale.Auth.Hosting.RequiresSecurity("bearerAuth"));
        });

        using var response = await server.Client.GetAsync("/openapi/v1.json");
        var document = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var paths = document.GetProperty("paths");
        static string Scheme(JsonElement operation) =>
            operation.GetProperty("security").EnumerateArray().Single().EnumerateObject().Single().Name;

        await Assert.That(Scheme(paths.GetProperty("/v1/{slug}/__test/users").GetProperty("get"))).IsEqualTo("ManagementBearer");
        await Assert.That(Scheme(paths.GetProperty("/ui/{slug}/admin/__test").GetProperty("get"))).IsEqualTo("SessionCookie");
        await Assert.That(Scheme(paths.GetProperty("/{slug}/oauth/userinfo").GetProperty("get"))).IsEqualTo("OAuthAccessBearer");
        await Assert.That(Scheme(paths.GetProperty("/v1/instances").GetProperty("post"))).IsEqualTo("OperatorBearer");
        await Assert.That(Scheme(paths.GetProperty("/scim/{directory}/__test").GetProperty("post"))).IsEqualTo("bearerAuth");
        // An explicitly anonymous endpoint under a protected prefix stays anonymous.
        var publicSecurity = paths.GetProperty("/v1/{slug}/__test/public").GetProperty("get").GetProperty("security");
        await Assert.That(publicSecurity.EnumerateArray().Single().EnumerateObject().Any()).IsFalse();

        // Exactly the referenced schemes are defined, each with its real mechanism.
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        await Assert.That(schemes.EnumerateObject().Select(scheme => scheme.Name).Order().ToArray())
            .IsEquivalentTo(ExpectedSchemes);
        await Assert.That(schemes.GetProperty("SessionCookie").GetProperty("in").GetString()).IsEqualTo("cookie");
        await Assert.That(schemes.GetProperty("SessionCookie").GetProperty("name").GetString()).IsEqualTo("tsauth_session");
        await Assert.That(schemes.GetProperty("ManagementBearer").GetProperty("scheme").GetString()).IsEqualTo("bearer");
    }

    [Test]
    public async Task Scalar_is_served_from_this_origin()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var redirect = await server.Client.GetAsync("/scalar");
        await Assert.That(redirect.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(new Uri(new Uri(server.BaseAddress, "/scalar"), redirect.Headers.Location!).AbsolutePath).IsEqualTo("/scalar/");
        using var response = await server.Client.GetAsync("/scalar/");
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("\"url\":\"openapi/v1.json\"");
        await Assert.That(html).DoesNotContain("cdn.jsdelivr.net");
        foreach (Match script in ScriptSource().Matches(html))
        {
            var source = new Uri(new Uri(server.BaseAddress, "/scalar/"), script.Groups["src"].Value);
            await Assert.That(source.Authority).IsEqualTo(server.BaseAddress.Authority);
            using var asset = await server.Client.GetAsync(source);
            await Assert.That(asset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task Scalar_renders_the_contract_without_browser_errors()
    {
        await using var server = await AuthTestServer.StartAsync();
        var (playwright, browser) = await Browser.LaunchAsync();
        using var _ = playwright;
        await using var __ = browser;
        var page = await browser.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => errors.Add(error);
        var external = new List<string>();
        page.Request += (_, request) =>
        {
            var url = new Uri(request.Url);
            var sameOrigin = url.Scheme == server.BaseAddress.Scheme
                && url.Host == server.BaseAddress.Host
                && url.Port == server.BaseAddress.Port;
            if (!sameOrigin && url.Scheme != "data")
            {
                external.Add(request.Url);
            }
        };

        await page.GotoAsync(new Uri(server.BaseAddress, "/scalar").ToString());
        try
        {
            await page.GetByRole(AriaRole.Heading, new() { Name = "TerraScale Auth API" }).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        }
        catch (TimeoutException)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "scalar-failure.png"), FullPage = true });
            throw new TimeoutException($"Scalar did not render. Errors: {string.Join(" | ", errors)}. Body: {await page.InnerTextAsync("body")}");
        }

        await Assert.That(errors).IsEmpty();
        await Assert.That(external).IsEmpty();
    }

    [GeneratedRegex("<script[^>]*\\ssrc=\"(?<src>[^\"]+)\"")]
    private static partial Regex ScriptSource();
}
