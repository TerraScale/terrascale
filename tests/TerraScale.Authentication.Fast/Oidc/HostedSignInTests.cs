using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Microsoft.Playwright;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class HostedSignInTests(TerraBaseFixture fixture)
{
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";

    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    public async Task A_wrong_code_is_refused_and_the_right_one_sets_a_locked_down_session_cookie()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        using var requested = await oidc.PostFormAsync(SignIn, new() { ["email"] = "alice@example.test", ["next"] = "https://evil.example/" });
        await Assert.That(await requested.Content.ReadAsStringAsync()).DoesNotContain("evil.example");
        var code = await oidc.Email.NextCodeAsync("alice@example.test");

        using var wrong = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = code == "000000" ? "111111" : "000000", ["next"] = string.Empty });
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();

        using var verified = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = code, ["next"] = "//evil.example/terrascale/oauth/authorize?x=1" });
        var page = await verified.Content.ReadAsStringAsync();
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page).Contains("You are signed in.");
        await Assert.That(page).DoesNotContain("evil.example");

        var cookie = SetCookieHeaderValue.ParseList([.. verified.Headers.GetValues("Set-Cookie")]).Single(value => value.Name == "tsauth_session");
        await Assert.That(cookie.Secure).IsTrue();
        await Assert.That(cookie.HttpOnly).IsTrue();
        await Assert.That(cookie.SameSite).IsEqualTo(SameSiteMode.Lax);
        await Assert.That(cookie.Path.ToString()).IsEqualTo("/");
        await Assert.That(cookie.Value.ToString()).IsEqualTo(OidcHarness.SessionCookie(verified)!);
    }

    [Test]
    public async Task Hosted_sign_up_creates_the_account_and_password_sign_in_and_reset_work()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600));
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Address = "new@example.test";
        const string Password = "correct horse battery";

        using var forged = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = Address, ["password"] = Password }, "https://evil.example");
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // Short, common and own-email passwords are refused before any code is mailed.
        foreach (var weak in new[] { "fourteen chars", "1234567890123456", Address })
        {
            using var refused = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = Address, ["password"] = weak, ["next"] = string.Empty });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Choose another password");
        }

        using var started = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = Address, ["password"] = Password, ["next"] = string.Empty });
        var verifyPage = await started.Content.ReadAsStringAsync();
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(verifyPage).Contains("<h1>Verify your email</h1>");
        var pending = PendingToken().Match(verifyPage).Groups[1].Value;
        await Assert.That(pending).IsNotEmpty();
        var code = await oidc.Email.NextCodeAsync(Address);

        var verify = new Dictionary<string, string> { ["email"] = Address, ["code"] = code == "000000" ? "111111" : "000000", ["pending_token"] = pending, ["next"] = string.Empty };
        using (var wrong = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up/verify", verify))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();
            await Assert.That(await wrong.Content.ReadAsStringAsync()).Contains(">Start over for a new code</a>");
        }

        using (var created = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up/verify", new(verify) { ["code"] = code }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await created.Content.ReadAsStringAsync()).Contains("You are signed in.");
            await Assert.That(OidcHarness.SessionCookie(created)).IsNotNull();
        }

        // The new account signs in with its password; the step without one only asks for it.
        using (var step = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/password", new() { ["email"] = Address }))
        {
            await Assert.That(await step.Content.ReadAsStringAsync()).Contains("id=\"sign-in-password\"");
            await Assert.That(OidcHarness.SessionCookie(step)).IsNull();
        }

        await Assert.That(await PasswordSignInAsync(oidc, Address, "wrong password!")).IsNull();
        await Assert.That(await PasswordSignInAsync(oidc, Address, Password)).IsNotNull();

        // Reset: the mail start cooldown is per address, so let it pass first.
        clock.Advance(TimeSpan.FromSeconds(61));
        using (var reset = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset", new() { ["email"] = Address }))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await reset.Content.ReadAsStringAsync()).Contains("<h1>Choose a new password</h1>");
        }

        var resetCode = await oidc.Email.NextCodeAsync(Address);
        using (var done = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new() { ["email"] = Address, ["code"] = resetCode, ["password"] = "a brand new passphrase" }))
        {
            await Assert.That(done.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await done.Content.ReadAsStringAsync()).Contains("<h1>Password updated</h1>");
        }

        await Assert.That(await PasswordSignInAsync(oidc, Address, Password)).IsNull();
        await Assert.That(await PasswordSignInAsync(oidc, Address, "a brand new passphrase")).IsNotNull();
    }

    [Test]
    public async Task A_password_reset_code_works_once_for_its_purpose_and_signs_the_user_out_everywhere()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Address = "alice@example.test";
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync(Address);
        var earlier = tokens.GetProperty("refresh_token").GetString()!;

        clock.Advance(HostedAuth.MailCooldown);
        using (var start = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset", new() { ["email"] = Address }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var (_, mail) = await oidc.Email.NextAsync();
        await Assert.That(mail.Subject).Contains("password reset code");
        var code = Regex.Match(mail.Text, @"\b[0-9]{6}\b").Value;
        var complete = new Dictionary<string, string> { ["email"] = Address, ["code"] = code, ["password"] = "brand new password" };

        // A reset code never signs in, and a password outside the policy keeps the code alive.
        using (var notSignIn = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = Address, ["code"] = code, ["next"] = string.Empty }))
        {
            await Assert.That(notSignIn.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        foreach (var weak in new[] { "short", "fourteen chars", "passwordpassword", new string('x', PasswordPolicy.MaxCharacters + 1) })
        {
            using var refused = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new(complete) { ["password"] = weak });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var reset = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", complete))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var again = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new(complete) { ["password"] = "a third password" }))
        {
            await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using (var stale = await oidc.RefreshAsync(earlier))
        {
            await Assert.That(await OidcHarness.ErrorAsync(stale)).IsEqualTo("invalid_grant");
        }

        // A sign-in code cannot reset the password either.
        clock.Advance(HostedAuth.MailCooldown);
        using (var signInStart = await oidc.PostFormAsync(SignIn, new() { ["email"] = Address, ["next"] = string.Empty }))
        {
            await Assert.That(signInStart.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var signInCode = await oidc.Email.NextCodeAsync(Address);
        using (var wrongPurpose = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new(complete) { ["code"] = signInCode, ["password"] = "a third password" }))
        {
            await Assert.That(wrongPurpose.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await Assert.That(await PasswordSignInAsync(oidc, Address, "brand new password")).IsNotNull();
    }

    // Starts a hosted sign-up and returns the pending token its verify page carries.

    // Every hosted email-code start shares the same mail cooldown.

    private static async Task<string?> PasswordSignInAsync(OidcHarness oidc, string address, string password)
    {
        using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/password", new() { ["email"] = address, ["password"] = password, ["next"] = string.Empty });
        return OidcHarness.SessionCookie(response);
    }

    [GeneratedRegex("name=\"pending_token\" value=\"([^\"]+)\"")]
    private static partial Regex PendingToken();

    [Test]
    public async Task Six_digit_entry_submits_the_real_code_and_password_visibility_is_accessible()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var violations = new List<string>();
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        await page.Locator("#sign-in-email").FillAsync("alice@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code", Exact = true }).ClickAsync();
        var code = await oidc.Email.NextCodeAsync("alice@example.test");
        await page.Locator(".otp input").First.FillAsync(code);
        await Assert.That(await page.Locator("input[name=code]").InputValueAsync()).IsEqualTo(code);
        await Assert.That(await page.Locator(".otp input").CountAsync()).IsEqualTo(6);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in" }).ClickAsync();
        await Assert.That(await page.Locator("h1").TextContentAsync()).IsEqualTo("Signed in");
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/sign-up");
        await page.Locator("#sign-up-password").FillAsync("synthetic test password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Show password" }).ClickAsync();
        await Assert.That(await page.Locator("#sign-up-password").GetAttributeAsync("type")).IsEqualTo("text");
        await page.GetByRole(AriaRole.Button, new() { Name = "Hide password" }).ClickAsync();
        await Assert.That(await page.Locator("#sign-up-password").GetAttributeAsync("type")).IsEqualTo("password");
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    /// Opens the real sign-in page. The browser never reaches the loopback listener directly: every request to the
    /// public origin is answered by the test server with its real headers, including the CSP, and anything else is refused.
    /// </summary>
    private static async Task<IPage> OpenSignInAsync(IBrowserContext context, AuthTestServer server, List<string> violations)
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
        await page.GotoAsync($"{OidcHarness.PublicOrigin}{SignIn}");
        return page;
    }

}
