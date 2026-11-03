using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>The shared loading-state script: referenced by the hosted pages, served as a static asset, and marking the clicked submitter busy without dropping its name/value from the post.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class HostedLoadingStateTests(TerraBaseFixture fixture)
{
    private const string DevicePage = $"/ui/{OidcIssuer.Slug}/device";
    private const string Script = "/admin/hosted.js";

    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    public async Task Hosted_pages_serve_the_loading_script_and_disable_the_clicked_submitter_while_posting()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();

        using var page = await oidc.Client.GetAsync($"/ui/{OidcIssuer.Slug}/sign-in");
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains($"<script src=\"{Script}\" defer></script>");

        using var script = await oidc.Client.GetAsync(Script);
        await Assert.That(script.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(script.Content.Headers.ContentType?.MediaType).IsEqualTo("text/javascript");
        await Assert.That(await script.Content.ReadAsStringAsync()).Contains("is-loading");

        using var deviceResponse = await oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = OidcIssuer.CliClientId,
            ["scope"] = "openid email offline_access",
        }));
        await Assert.That(deviceResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var userCode = JsonSerializer.Deserialize<JsonElement>(await deviceResponse.Content.ReadAsStringAsync()).GetProperty("user_code").GetString()!;

        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        var violations = new List<string>();
        var tab = await OpenRoutedAsync(context, oidc.Server, violations);
        await tab.GotoAsync($"{OidcHarness.PublicOrigin}/ui/{OidcIssuer.Slug}/sign-in?next={Uri.EscapeDataString($"{DevicePage}?user_code={Uri.EscapeDataString(userCode)}")}");
        await tab.Locator("#sign-in-email").FillAsync("alice@example.test");
        await tab.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code", Exact = true }).ClickAsync();
        await tab.Locator(".otp input").First.FillAsync(await oidc.Email.NextCodeAsync("alice@example.test"));
        await tab.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in" }).ClickAsync();
        await tab.Locator(".device-code").WaitForAsync();
        await Assert.That(await tab.Locator(".device-code").TextContentAsync()).IsEqualTo(userCode);

        var approve = tab.GetByRole(AriaRole.Button, new() { Name = "Approve" });
        var busy = await approve.EvaluateAsync<JsonElement>(@"button => {
          const form = button.form, fields = [];
          form.addEventListener('submit', event => event.preventDefault(), { once: true, capture: true });
          button.click();
          for (const entry of new FormData(form).entries()) fields.push(entry[0] + '=' + entry[1]);
          return { loading: button.classList.contains('is-loading'), busy: button.getAttribute('aria-busy'), disabled: button.disabled, fields: fields };
        }");
        await Assert.That(busy.GetProperty("loading").GetBoolean()).IsTrue();
        await Assert.That(busy.GetProperty("busy").GetString()).IsEqualTo("true");
        await Assert.That(busy.GetProperty("disabled").GetBoolean()).IsTrue();
        var fields = busy.GetProperty("fields").EnumerateArray().Select(field => field.GetString()).ToArray();
        await Assert.That(fields).Contains("decision=approve");

        // The real click, with the submitter disabled and its name/value carried by the hidden field, still approves the device.
        await tab.GotoAsync($"{OidcHarness.PublicOrigin}{DevicePage}?user_code={Uri.EscapeDataString(userCode)}");
        await tab.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
        await Assert.That(await tab.Locator("h1").TextContentAsync()).IsEqualTo("Device connected");
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>Routes every browser request through the real server with its real headers, exactly as the hosted-page browser journey does.</summary>
    private static async Task<IPage> OpenRoutedAsync(IBrowserContext context, AuthTestServer server, List<string> violations)
    {
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (url.Host != "auth.test.invalid")
            {
                await route.AbortAsync();
                return;
            }

            using var request = new HttpRequestMessage(new HttpMethod(route.Request.Method), url.PathAndQuery);
            if (route.Request.PostDataBuffer is { } body)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", route.Request.Headers["content-type"]);
            }

            foreach (var header in route.Request.Headers.Where(header => header.Key is "origin" or "cookie"))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await server.Client.SendAsync(request);
            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(header => header.Key is not ("Content-Length" or "Transfer-Encoding"))
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value));
            await route.FulfillAsync(new() { Status = (int)response.StatusCode, Headers = headers, BodyBytes = await response.Content.ReadAsByteArrayAsync() });
        });
        var page = await context.NewPageAsync();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.Ordinal))
            {
                violations.Add(message.Text);
            }
        };
        return page;
    }
}
