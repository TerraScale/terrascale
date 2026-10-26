using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

public sealed partial class HostedSignInTests
{
    [Test]
    [Arguments(false, 390)]
    [Arguments(true, 390)]
    [Arguments(false, 1280)]
    [Arguments(true, 1280)]
    public async Task Signup_resends_in_context_with_accessible_hints_and_script_free_forms(bool javaScript, int width)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new() { JavaScriptEnabled = javaScript, ViewportSize = new() { Width = width, Height = 844 } });
        var violations = new List<string>();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/sign-up");
        await Assert.That(await page.Locator("#sign-up-password").GetAttributeAsync("aria-describedby")).Contains("password-requirements");
        await page.Locator("#sign-up-email").FillAsync("resend@example.test");
        await page.Locator("#sign-up-password").FillAsync("synthetic test password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).ClickAsync();
        await page.Locator("#sign-up-code").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        _ = await oidc.Email.NextCodeAsync("resend@example.test");
        var pending = await page.Locator("input[name=pending_token]").First.InputValueAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Send a new code", Exact = true }).ClickAsync();
        await Assert.That(await page.Locator("#form-error").TextContentAsync()).Contains("Try again in");
        await Assert.That(await page.Locator("input[name=pending_token]").First.InputValueAsync()).IsEqualTo(pending);
        await Assert.That(await page.Locator("#retry-hint time").GetAttributeAsync("datetime")).IsNotNull();
        await Assert.That(await page.EvaluateAsync<int>("document.documentElement.scrollWidth")).IsLessThanOrEqualTo(width);
        clock.UtcNow += HostedAuth.MailCooldown;
        await page.GetByRole(AriaRole.Button, new() { Name = "Send a new code", Exact = true }).ClickAsync();
        var latest = await oidc.Email.NextCodeAsync("resend@example.test");
        await Assert.That(await page.Locator("input[name=pending_token]").First.InputValueAsync()).IsEqualTo(pending);
        await page.Locator(javaScript ? ".otp input" : "#sign-up-code").First.FillAsync(latest);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify email", Exact = true }).ClickAsync();
        await Assert.That(await page.Locator("h1").TextContentAsync()).IsEqualTo("Signed in");
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments(390)]
    [Arguments(1280)]
    public async Task Device_consent_discloses_the_bound_permissions_before_keyboard_approval(int width)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        using var requested = await oidc.Client.PostAsync("/terrascale/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = OidcIssuer.CliClientId, ["scope"] = "openid email documents:read" }));
        var device = await OidcHarness.JsonAsync(requested);
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = 844 } });
        await context.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "tsauth_session", Value = session, Url = OidcHarness.PublicOrigin, HttpOnly = true, Secure = true, SameSite = SameSiteAttribute.Lax }]);
        var violations = new List<string>();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/device?user_code={Uri.EscapeDataString(device.GetProperty("user_code").GetString()!)}");
        await Assert.That(await page.Locator(".facts").TextContentAsync()).Contains("ts-cli");
        var permissions = await page.GetByRole(AriaRole.List, new() { Name = "Requested permissions" }).TextContentAsync();
        await Assert.That(permissions).Contains("Read collections and documents and run queries (documents:read)");
        await Assert.That(permissions).Contains("Read your email address (email)");
        await Assert.That(permissions).DoesNotContain("Write collections");
        await Assert.That(await page.EvaluateAsync<int>("document.documentElement.scrollWidth")).IsLessThanOrEqualTo(width);
        await page.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Device connected", Exact = true }).WaitForAsync();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task A_pending_native_submission_announces_busy_blocks_duplicates_and_retains_the_submitter()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        var violations = new List<string>();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        await page.Locator("#sign-in-email").FillAsync("nobody@example.test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await page.RouteAsync("**/sign-in/password", async route =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult();
            await release.Task;
            await route.FallbackAsync();
        });
        var submission = page.GetByRole(AriaRole.Button, new() { Name = "Use a password instead", Exact = true }).ClickAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(await page.Locator("form[aria-busy=true]").CountAsync()).IsEqualTo(1);
            await Assert.That(await page.Locator("#submission-status").TextContentAsync()).Contains("Submitting");
            await page.EvaluateAsync("document.querySelector('form[action=\"/ui/terrascale/sign-in\"]').requestSubmit()");
            await Assert.That(requests).IsEqualTo(1);
        }
        finally
        {
            release.TrySetResult();
        }

        await submission;
        await Assert.That(await page.Locator("h1").TextContentAsync()).IsEqualTo("Enter your password");
        await page.GoBackAsync();
        await Assert.That(await page.Locator("form[aria-busy=true]").CountAsync()).IsEqualTo(0);
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Reset_resend_keeps_the_verification_step_email_and_safe_next_on_cooldown()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Next = "/ui/terrascale/security";
        await oidc.CreateUserAsync("reset@example.test");
        using var start = await oidc.PostFormAsync("/ui/terrascale/reset", new() { ["email"] = "reset@example.test", ["next"] = Next });
        _ = await oidc.Email.NextCodeAsync("reset@example.test");
        using var limited = await oidc.PostFormAsync("/ui/terrascale/reset", new() { ["email"] = "reset@example.test", ["next"] = Next, ["resend"] = "true" });
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        var html = await limited.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("id=\"reset-code\"");
        await Assert.That(html).Contains("name=\"email\" value=\"reset@example.test\"");
        await Assert.That(html).Contains($"name=\"next\" value=\"{Next}\"");
        await Assert.That(html).Contains("aria-describedby=\"password-requirements form-error\"");
        using var forged = await oidc.PostFormAsync("/ui/terrascale/sign-up/resend", new() { ["email"] = "reset@example.test" }, origin: "https://evil.example");
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        clock.UtcNow += limited.Headers.RetryAfter!.Delta!.Value;
        using var resent = await oidc.PostFormAsync("/ui/terrascale/reset", new() { ["email"] = "reset@example.test", ["next"] = Next, ["resend"] = "true" });
        await Assert.That(resent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await oidc.Email.NextCodeAsync("reset@example.test")).IsNotEmpty();
    }

    [Test]
    public async Task A_superseded_signup_token_cannot_overwrite_the_new_pending_signup_when_resending()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Email = "superseded@example.test";
        var old = await SignUpAsync(oidc, Email, "old synthetic password");
        _ = await oidc.Email.NextCodeAsync(Email);
        clock.UtcNow += HostedAuth.MailCooldown;
        var current = await SignUpAsync(oidc, Email, "new synthetic password");
        var code = await oidc.Email.NextCodeAsync(Email);
        clock.UtcNow += HostedAuth.MailCooldown;
        using var resend = await oidc.PostFormAsync("/ui/terrascale/sign-up/resend", new() { ["email"] = Email, ["pending_token"] = old, ["next"] = "https://evil.example" });
        await Assert.That(resend.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await resend.Content.ReadAsStringAsync()).DoesNotContain("https://evil.example");
        // A sentinel on the same real mail queue observes completion without sleeps or email delivery.
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oidc.Server.App.Services.GetRequiredService<SignInMail>().EnqueueIssue(() =>
        {
            processed.TrySetResult();
            return Task.FromResult<IssuedPasscode?>(null);
        });
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using var verify = await SignUpVerifyAsync(oidc, Email, code, current);
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await PasswordSignInAsync(oidc, Email, "new synthetic password")).IsNotNull();
        await Assert.That(await PasswordSignInAsync(oidc, Email, "old synthetic password")).IsNull();
    }
}
