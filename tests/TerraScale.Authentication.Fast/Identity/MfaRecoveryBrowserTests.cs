using Microsoft.Playwright;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

public sealed partial class MfaSignInTests
{
    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    public async Task Browser_recovery_codes_sign_in_and_disable_the_factor_without_numeric_truncation()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync(new Bogus.Faker().Internet.Email().ToLowerInvariant());
        var (_, codes, _) = await EnrolAsync(oidc, await oidc.SignInAsync(user.Email), clock);
        clock.UtcNow += HostedAuth.MailCooldown;
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        await RouteBrowserAsync(context, oidc.Server);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/sign-in");
        await page.Locator("#sign-in-email").FillAsync(user.Email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code", Exact = true }).ClickAsync();
        var code = await oidc.Email.NextCodeAsync(user.Email);
        var refused = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Send a new code", Exact = true }).ClickAsync(),
            response => response.Url.EndsWith("/ui/terrascale/sign-in", StringComparison.Ordinal) && response.Request.Method == "POST");
        await Assert.That(refused.Status).IsEqualTo(429);
        await page.Locator(".otp input").First.FillAsync(code);
        await Assert.That(await page.Locator(".otp input").CountAsync()).IsEqualTo(6);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in" }).ClickAsync();

        var recovery = page.GetByLabel("Authentication code", new() { Exact = true });
        await recovery.FillAsync(codes[0]);
        await Assert.That(await recovery.InputValueAsync()).IsEqualTo(codes[0]);
        await Assert.That(await page.Locator(".otp input").CountAsync()).IsEqualTo(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in" }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Signed in");
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/security");
        await Microsoft.Playwright.Assertions.Expect(page.Locator(".facts dd").Last).ToHaveTextAsync("7");
        var disable = page.GetByLabel("Code to turn off two-factor", new() { Exact = true });
        await disable.FillAsync(codes[1]);
        await Assert.That(await disable.InputValueAsync()).IsEqualTo(codes[1]);
        await Assert.That(await page.Locator(".otp input").CountAsync()).IsEqualTo(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Turn off two-factor authentication" }).ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Two-factor authentication is off.");
    }

    [Test]
    public async Task Browser_refused_requests_have_a_safe_sign_in_recovery_action()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        await RouteBrowserAsync(context, oidc.Server);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/terrascale/oauth/authorize");
        var recovery = page.GetByRole(AriaRole.Link, new() { Name = "Back to sign in" });
        await Microsoft.Playwright.Assertions.Expect(recovery).ToHaveAttributeAsync("href", "/ui/terrascale/sign-in");
        await recovery.ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(page.GetByLabel("Email", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task RouteBrowserAsync(IBrowserContext context, AuthTestServer server)
    {
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (url.Host != "auth.test.invalid")
            {
                await route.AbortAsync();
                return;
            }

            var browserHeaders = await route.Request.AllHeadersAsync();
            using var request = new HttpRequestMessage(new HttpMethod(route.Request.Method), url.PathAndQuery);
            if (route.Request.PostDataBuffer is { } body)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", browserHeaders["content-type"]);
            }

            foreach (var header in browserHeaders.Where(header => header.Key is "origin" or "cookie"))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await server.Client.SendAsync(request);
            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(header => header.Key is not ("Content-Length" or "Transfer-Encoding"))
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value));
            await route.FulfillAsync(new() { Status = (int)response.StatusCode, Headers = headers, BodyBytes = await response.Content.ReadAsByteArrayAsync() });
        });
    }
}
