using System.Net;
using Microsoft.Playwright;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

public sealed partial class HostedSignInTests
{
    [Test]
    public async Task Hosted_errors_show_the_response_request_id_and_codes_offer_a_fresh_attempt()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        using var navigation = new HttpRequestMessage(HttpMethod.Get, "/ui/terrascale/not-a-page");
        navigation.Headers.Accept.ParseAdd("text/html");
        using var notFound = await oidc.Client.SendAsync(navigation);
        await Assert.That(notFound.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var rejected = await oidc.PostFormAsync(SignIn, new() { ["email"] = "reader@example.test" }, "https://untrusted.example");
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var localError = await oidc.Client.GetAsync("/terrascale/oauth/authorize?client_id=unknown&response_type=code");
        await Assert.That(await localError.Content.ReadAsStringAsync()).Contains("<h1>Request refused</h1>");
        using var wrong = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "reader@example.test", ["code"] = "000000" });
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var wrongPage = await wrong.Content.ReadAsStringAsync();
        await Assert.That(wrongPage).Contains("This code is wrong, expired, or already used. Check the latest email, or request a new code.");
        await Assert.That(wrongPage).Contains(">Send a new code</button>");
        foreach (var response in new[] { notFound, rejected, localError, wrong })
        {
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains($"Request ID: {response.Headers.GetValues("X-Request-ID").Single()}");
        }
    }

    [Test]
    public async Task Default_footer_leaves_sign_in_open_and_password_has_its_own_label()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        var violations = new List<string>();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        foreach (var (label, url) in new[]
        {
            ("Status", "https://status.terrascale.tech"),
            ("Docs", "https://docs.terrascale.tech"),
            ("Privacy", "https://docs.terrascale.tech/legal/privacy"),
            ("Terms", "https://docs.terrascale.tech/legal/terms"),
        })
        {
            var link = page.GetByRole(AriaRole.Link, new() { Name = label, Exact = true });
            await Assert.That(await link.GetAttributeAsync("href")).IsEqualTo(url);
            await Assert.That(await link.GetAttributeAsync("target")).IsEqualTo("_blank");
            await Assert.That(await link.GetAttributeAsync("rel")).IsEqualTo("noopener noreferrer");
        }

        await page.Locator("#sign-in-email").FillAsync("reader@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Use a password instead" }).ClickAsync();
        await Assert.That(await page.GetByLabel("Password", new() { Exact = true }).GetAttributeAsync("id")).IsEqualTo("sign-in-password");
        await Assert.That(await page.Locator("label a").CountAsync()).IsEqualTo(0);
        await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = "Forgot password?" }).CountAsync()).IsEqualTo(1);
        await Assert.That(violations).IsEmpty();
    }
}
