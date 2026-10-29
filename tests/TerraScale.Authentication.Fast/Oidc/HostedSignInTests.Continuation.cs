using System.Net;
using Microsoft.AspNetCore.WebUtilities;
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
        { ["client_id"] = OidcIssuer.CliClientId, ["scope"] = "openid email profile offline_access" }));
        var device = await OidcHarness.JsonAsync(requested);
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = 844 } });
        await context.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "tsauth_session", Value = session, Url = OidcHarness.PublicOrigin, HttpOnly = true, Secure = true, SameSite = SameSiteAttribute.Lax }]);
        var violations = new List<string>();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        // The shared proxy uses Request.Headers, which can omit security-related headers.
        // Preserve the browser's actual Origin/cookie on the native approval POST.
        await page.RouteAsync("**/ui/terrascale/device", route => ForwardContinuationFormAsync(route, oidc.Server));
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/device?user_code={Uri.EscapeDataString(device.GetProperty("user_code").GetString()!)}");
        await Assert.That(await page.Locator(".facts").TextContentAsync()).Contains("ts-cli");
        var permissions = await page.GetByRole(AriaRole.List, new() { Name = "Requested permissions" }).TextContentAsync();
        await Assert.That(permissions).Contains("Read your profile information (profile)");
        await Assert.That(permissions).Contains("Read your email address (email)");
        await Assert.That(permissions).Contains("Keep access through refresh tokens until access expires or is revoked (offline_access)");
        await Assert.That(permissions).DoesNotContain("Write collections");
        await Assert.That(await page.EvaluateAsync<int>("document.documentElement.scrollWidth")).IsLessThanOrEqualTo(width);
        await page.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).FocusAsync();
        var approved = await page.RunAndWaitForResponseAsync(() => page.Keyboard.PressAsync("Enter"),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == DeviceFlow.PagePath);
        var posted = QueryHelpers.ParseQuery(approved.Request.PostData ?? string.Empty);
        await Assert.That(posted["decision"] == "approve").IsTrue();
        await Assert.That(posted["user_code"] == device.GetProperty("user_code").GetString()).IsTrue();
        var headers = await approved.Request.AllHeadersAsync();
        await Assert.That(headers.TryGetValue("origin", out var origin) && origin == OidcHarness.PublicOrigin).IsTrue();
        await Assert.That(headers.TryGetValue("cookie", out var cookie) && cookie.Contains("tsauth_session=", StringComparison.Ordinal)).IsTrue();
        var approvalHtml = await approved.TextAsync();
        await Assert.That(approved.Status).IsEqualTo((int)HttpStatusCode.OK).Because(
            $"wrong-code={approvalHtml.Contains("That code is wrong or expired", StringComparison.Ordinal)}; "
            + $"unavailable={approvalHtml.Contains("Device request unavailable", StringComparison.Ordinal)}; "
            + $"protocol-error={approvalHtml.Contains("The device could not be approved", StringComparison.Ordinal)}; "
            + $"invalid-sign-in-scope={approvalHtml.Contains("A sign-in grants only openid, email, profile and offline_access.", StringComparison.Ordinal)}; "
            + $"lineage-conflict={approvalHtml.Contains(CredentialLineage.ConflictNotice, StringComparison.Ordinal)}");
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
            try
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await ForwardContinuationFormAsync(route, oidc.Server);
            }
            catch (TimeoutException)
            {
                await route.AbortAsync();
            }
            catch (PlaywrightException) when (page.IsClosed)
            {
                // Failure cleanup closes the page after releasing its held request.
            }
        });
        try
        {
            // Observe both native button activations in the current document, before navigation can
            // make another DOM query wait for the response this test deliberately holds.
            var observed = await page.EvaluateAsync<bool[]>("""
() => {
  var form = document.querySelector('form[action="/ui/terrascale/sign-in"]');
  var button = form.querySelector('button[formaction="/ui/terrascale/sign-in/password"]');
  var attempts = [];
  form.addEventListener('submit', event => attempts.push({ cancelled: event.defaultPrevented, submitter: event.submitter === button }));
  button.click();
  var busy = form.getAttribute('aria-busy') === 'true';
  var announced = document.getElementById('submission-status').textContent.includes('Submitting');
  button.click();
  return [busy, announced, attempts.length === 2, attempts[0]?.cancelled === false,
    attempts[1]?.cancelled === true, attempts.every(attempt => attempt.submitter)];
}
""").WaitAsync(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var holds in observed)
            {
                await Assert.That(holds).IsTrue();
            }

            await Assert.That(requests).IsEqualTo(1);
            var submission = page.WaitForResponseAsync(response => response.Request.Method == "POST"
                && new Uri(response.Url).AbsolutePath == "/ui/terrascale/sign-in/password", new() { Timeout = 15000 });
            release.TrySetResult();
            await Assert.That((await submission).Status).IsEqualTo((int)HttpStatusCode.OK);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Enter your password", Exact = true }).WaitForAsync();
            await page.GoBackAsync();
            await Assert.That(await page.Locator("form[aria-busy=true]").CountAsync()).IsEqualTo(0);
            await Assert.That(violations).IsEmpty();
        }
        finally
        {
            release.TrySetResult();
            await page.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task ForwardContinuationFormAsync(IRoute route, AuthTestServer server)
    {
        Console.WriteLine("Mobile forwarding headers start");
        var headers = await route.Request.AllHeadersAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"Mobile forwarding headers complete cookie={headers.ContainsKey("cookie")} origin={headers.ContainsKey("origin")}");
        using var request = new HttpRequestMessage(new HttpMethod(route.Request.Method), new Uri(route.Request.Url).PathAndQuery);
        if (route.Request.PostDataBuffer is { } body)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", headers["content-type"]);
        }

        foreach (var header in headers.Where(header => header.Key is "origin" or "cookie"))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Console.WriteLine("Mobile forwarding server start");
        using var response = await server.Client.SendAsync(request, deadline.Token);
        Console.WriteLine($"Mobile forwarding server complete {(int)response.StatusCode}");
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Content-Length" or "Transfer-Encoding"))
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value));
        await route.FulfillAsync(new()
        {
            Status = (int)response.StatusCode,
            Headers = responseHeaders,
            BodyBytes = await response.Content.ReadAsByteArrayAsync(deadline.Token),
        });
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
        // Drain the durable worker before checking that the superseded resend issued no mail.
        await oidc.Server.App.Services.GetRequiredService<SignInMail>().StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using var verify = await SignUpVerifyAsync(oidc, Email, code, current);
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await PasswordSignInAsync(oidc, Email, "new synthetic password")).IsNotNull();
        await Assert.That(await PasswordSignInAsync(oidc, Email, "old synthetic password")).IsNull();
    }
}
