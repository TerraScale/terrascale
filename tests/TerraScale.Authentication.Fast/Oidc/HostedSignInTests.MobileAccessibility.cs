using System.Security.Cryptography;
using Bogus;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Microsoft.Playwright;
using OtpNet;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

public sealed partial class HostedSignInTests
{
    // Browser/device emulation is automated acceptance, not physical-device or assistive-technology proof.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Mobile_hosted_password_signup_and_reset_have_accessible_native_forms(bool webKit)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchMobileAsync(playwright, webKit);
        await using var context = await browser.NewContextAsync(playwright.Devices[webKit ? "iPhone 13" : "Pixel 5"]);
        var page = await OpenMobileAsync(context, oidc.Server);
        var email = new Faker().Internet.Email().ToLowerInvariant();
        await MobileFieldAsync(page, "sign-in-email", "email", "username", "Email");
        await MobileAuditAsync(page);
        await page.Locator("#sign-in-email").FillAsync(email);
        await MobilePasswordStepAsync(page);
        await MobileFieldAsync(page, "sign-in-password", "password", "current-password", "Password");
        await Assert.That(await page.Locator(".chip").InnerTextAsync()).Contains(email);
        await Assert.That(await page.Locator("input[name=email]").First.InputValueAsync() == email).IsTrue();
        await Assert.That(await page.Locator("form:has(#sign-in-password) input[name=email]").GetAttributeAsync("autocomplete")).IsEqualTo("username");
        await Assert.That(await page.Locator("form:has(#sign-in-password) input[name=email]").GetAttributeAsync("id")).IsEqualTo("sign-in-username");
        await MobileAuditAsync(page);
        await page.Locator("#sign-in-password").FillAsync("deliberately incorrect password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await MobileErrorAsync(page, "#sign-in-password", "Wrong email or password");
        await MobileAuditAsync(page);

        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/sign-up");
        await MobileFieldAsync(page, "sign-up-email", "email", "username", "Email");
        await MobileFieldAsync(page, "sign-up-password", "password", "new-password", "Password");
        await Assert.That(await page.Locator("#sign-up-password").GetAttributeAsync("aria-describedby")).Contains("password-requirements");
        await page.Locator("#sign-up-password").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        var show = page.GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true });
        await Assert.That(await show.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
        await Assert.That(await show.EvaluateAsync<bool>("el => getComputedStyle(el).outlineStyle !== 'none' && parseFloat(getComputedStyle(el).outlineWidth) > 0")).IsTrue();
        await page.Keyboard.PressAsync("Space");
        var hide = page.GetByRole(AriaRole.Button, new() { Name = "Hide password", Exact = true });
        await Assert.That(await hide.GetAttributeAsync("aria-pressed")).IsEqualTo("true");
        await Assert.That(await hide.GetAttributeAsync("aria-controls")).IsEqualTo("sign-up-password");
        await page.Keyboard.PressAsync("Space");
        await Assert.That(await page.Locator("#sign-up-password").GetAttributeAsync("type")).IsEqualTo("password");
        await MobileAuditAsync(page);
        await page.Locator("#sign-up-email").FillAsync(email);
        await page.Locator("#sign-up-password").FillAsync("synthetic mobile acceptance password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).ClickAsync();
        await page.Locator("#sign-up-code").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var signupCode = await oidc.Email.NextCodeAsync(email);
        var pending = await page.Locator("input[name=pending_token]").First.InputValueAsync();
        await MobileCodeSemanticsAsync(page, "sign-up-code", "Verification code");
        await MobileAuditAsync(page);
        await FillMobileCodeAsync(page, "sign-up-code", signupCode == "000000" ? "111111" : "000000");
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify email", Exact = true }).ClickAsync();
        await MobileErrorAsync(page, "#sign-up-code-digit", "code");
        await Assert.That(await page.Locator("input[name=pending_token]").First.InputValueAsync() == pending).IsTrue();
        await MobileAuditAsync(page);
        await FillMobileCodeAsync(page, "sign-up-code", signupCode);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify email", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Signed in", Exact = true }).WaitForAsync();
        await Assert.That((await context.CookiesAsync()).Any(cookie => cookie.Name == "tsauth_session" && cookie.Value.Length > 0)).IsTrue();

        clock.UtcNow += HostedAuth.MailCooldown;
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/reset");
        await MobileFieldAsync(page, "reset-email", "email", "username", "Email");
        await MobileAuditAsync(page);
        await page.Locator("#reset-email").FillAsync(email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a reset code", Exact = true }).ClickAsync();
        await page.Locator("#reset-code").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var resetCode = await oidc.Email.NextCodeAsync(email);
        await MobileCodeSemanticsAsync(page, "reset-code", "Reset code");
        await MobileFieldAsync(page, "reset-password", "password", "new-password", "New password");
        await MobileAuditAsync(page);
        await FillMobileCodeAsync(page, "reset-code", resetCode == "000000" ? "111111" : "000000");
        await page.Locator("#reset-password").FillAsync("replacement synthetic acceptance password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Set new password", Exact = true }).ClickAsync();
        await MobileErrorAsync(page, "#reset-code-digit", "code");
        await MobileAuditAsync(page);
        await FillMobileCodeAsync(page, "reset-code", resetCode);
        await page.Locator("#reset-password").FillAsync("replacement synthetic acceptance password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Set new password", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Password updated", Exact = true }).WaitForAsync();
        await MobileAuditAsync(page);

        // Reset revokes the browser session; device entry requires a new authenticated session.
        await page.GotoAsync($"{OidcHarness.PublicOrigin}{SignIn}");
        await page.Locator("#sign-in-email").FillAsync(email);
        await MobilePasswordStepAsync(page);
        await page.Locator("#sign-in-password").FillAsync("replacement synthetic acceptance password");
        var signedIn = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == SignIn + "/password"
                && !string.IsNullOrEmpty(QueryHelpers.ParseQuery(response.Request.PostData ?? string.Empty)["password"]));
        await page.GetByRole(AriaRole.Heading, new() { Name = "Signed in", Exact = true }).WaitForAsync();
        await page.WaitForLoadStateAsync();
        var responseCookies = SetCookieHeaderValue.ParseList([.. await signedIn.HeaderValuesAsync("set-cookie")]);
        var issued = responseCookies.SingleOrDefault(cookie => cookie.Name == "tsauth_session");
        var browserCookies = await context.CookiesAsync();
        Console.WriteLine($"Mobile cookie issued={issued is not null} count={browserCookies.Count} accepted={browserCookies.Any(cookie => cookie.Name == "tsauth_session" && cookie.Value == issued?.Value.ToString())}");
        await Assert.That(issued is not null && browserCookies.Any(cookie =>
            cookie.Name == "tsauth_session" && cookie.Value == issued.Value.ToString())).IsTrue();
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/device");
        await MobileFieldAsync(page, "device-code", "user_code", "one-time-code", "Device code");
        await MobileAuditAsync(page);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Mobile_email_code_and_mfa_announce_errors_and_accept_the_real_second_factor(bool webKit)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync(new Faker().Internet.Email().ToLowerInvariant());
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var enrollment = (await mfa.BeginAsync(user))!;
        var secret = Base32Encoding.ToBytes(enrollment.Secret);
        string recovery;
        try
        {
            var activated = await mfa.ConfirmAsync(user, new Totp(secret).ComputeTotp(clock.UtcNow.UtcDateTime));
            await Assert.That(activated is not null).IsTrue();
            recovery = activated!.Codes[0];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchMobileAsync(playwright, webKit);
        await using var context = await browser.NewContextAsync(playwright.Devices[webKit ? "iPhone 13" : "Pixel 5"]);
        var page = await OpenMobileAsync(context, oidc.Server);
        await page.Locator("#sign-in-email").FillAsync(user.Email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code", Exact = true }).ClickAsync();
        await page.Locator("#sign-in-code").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var code = await oidc.Email.NextCodeAsync(user.Email);
        await MobileCodeSemanticsAsync(page, "sign-in-code", "Sign-in code");
        await MobileAuditAsync(page);
        await FillMobileCodeAsync(page, "sign-in-code", code == "000000" ? "111111" : "000000");
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Alert).WaitForAsync();
        await MobileErrorAsync(page, "#sign-in-code-digit", "code");
        await MobileAuditAsync(page);
        await FillMobileCodeAsync(page, "sign-in-code", code);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in", Exact = true }).ClickAsync();
        await page.Locator("#mfa-code").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await MobileCodeSemanticsAsync(page, "mfa-code", "Authentication code");
        await Assert.That(await page.Locator("input[name=challenge]").InputValueAsync() is { Length: > 0 }).IsTrue();
        await Assert.That((await context.CookiesAsync()).Any(cookie => cookie.Name == "tsauth_session")).IsFalse();
        await MobileAuditAsync(page);
        // Recovery codes remain text, rather than being forced through the six-digit control.
        await Assert.That(await page.Locator("#mfa-code").IsVisibleAsync()).IsTrue();
        await page.Locator("#mfa-code").FillAsync("invalid-recovery-code");
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in", Exact = true }).ClickAsync();
        await MobileErrorAsync(page, "#mfa-code", "code");
        await MobileAuditAsync(page);
        await Assert.That(await page.Locator("#mfa-code").GetAttributeAsync("aria-describedby")).Contains("mfa-hint");
        // Use a real unused recovery proof; never include it in assertion diagnostics.
        await page.Locator("#mfa-code").FillAsync(recovery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Signed in", Exact = true }).WaitForAsync();
        await Assert.That((await context.CookiesAsync()).Any(cookie => cookie.Name == "tsauth_session" && cookie.Value.Length > 0)).IsTrue();
        await MobileAuditAsync(page);
    }

    private static Task<IBrowser> LaunchMobileAsync(IPlaywright playwright, bool webKit) =>
        (webKit ? playwright.Webkit : playwright.Chromium).LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = webKit ? null : Environment.GetEnvironmentVariable("TS_AUTH_CHROMIUM_PATH"),
        });

    private static async Task<IPage> OpenMobileAsync(IBrowserContext context, AuthTestServer server)
    {
        await context.RouteAsync("**/*", async route =>
        {
            if (new Uri(route.Request.Url).Host != "auth.test.invalid")
            {
                await route.AbortAsync();
                return;
            }

            // The existing forwarder uses AllHeadersAsync, preserving actual Origin and cookies.
            Console.WriteLine($"Mobile route start {route.Request.Method} {new Uri(route.Request.Url).AbsolutePath}");
            try
            {
                await ForwardContinuationFormAsync(route, server);
            }
            catch (Exception error)
            {
                Console.WriteLine($"Mobile forwarding failed {error.GetType().Name}");
                await route.AbortAsync();
                return;
            }

            Console.WriteLine($"Mobile route fulfilled {route.Request.Method} {new Uri(route.Request.Url).AbsolutePath}");
        });
        var page = await context.NewPageAsync();
        page.Response += (_, response) => Console.WriteLine($"Mobile response {response.Status} {new Uri(response.Url).AbsolutePath}");
        page.RequestFailed += (_, request) => Console.WriteLine($"Mobile failed {request.Method} {new Uri(request.Url).AbsolutePath}");
        page.DOMContentLoaded += (_, _) => Console.WriteLine("Mobile DOM ready");
        page.Load += (_, _) => Console.WriteLine("Mobile load");
        var response = await page.GotoAsync($"{OidcHarness.PublicOrigin}{SignIn}");
        await Assert.That(response is not null && response.Headers.ContainsKey("content-security-policy")).IsTrue();
        return page;
    }

    private static async Task MobilePasswordStepAsync(IPage page)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/sign-in/password", async route =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.FallbackAsync();
        });
        try
        {
            var observed = await page.EvaluateAsync<bool>("""
                () => {
                  const button = [...document.querySelectorAll('button')].find(b => b.formAction.endsWith('/sign-in/password'));
                  button.click();
                  return button.form.getAttribute('aria-busy') === 'true' && button.getAttribute('aria-disabled') === 'true'
                    && document.querySelector('[role=status][aria-live=polite]').textContent.includes('Submitting');
                }
                """);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(observed).IsTrue();
        }
        finally
        {
            release.TrySetResult();
        }

        await page.Locator("#sign-in-password").WaitForAsync();
        await page.UnrouteAsync("**/sign-in/password");
        await Assert.That(await page.Locator("form[aria-busy=true]").CountAsync()).IsEqualTo(0);
    }

    private static async Task MobileFieldAsync(IPage page, string id, string name, string autocomplete, string label)
    {
        var field = page.Locator($"#{id}");
        await Assert.That(await field.GetAttributeAsync("name")).IsEqualTo(name);
        await Assert.That(await field.GetAttributeAsync("autocomplete")).IsEqualTo(autocomplete);
        await Assert.That(await page.Locator($"label[for='{id}'],label[for='{id}-digit']").InnerTextAsync()).Contains(label);
        if (await field.IsVisibleAsync())
        {
            await Assert.That(await page.GetByLabel(label, new() { Exact = false }).And(field).CountAsync()).IsEqualTo(1);
        }
    }

    private static async Task MobileCodeSemanticsAsync(IPage page, string id, string label)
    {
        await MobileFieldAsync(page, id, "code", "one-time-code", label);
        if (await page.Locator(".otp").CountAsync() > 0)
        {
            await Assert.That(await page.GetByRole(AriaRole.Group, new() { Name = label, Exact = true }).CountAsync()).IsEqualTo(1);
            await Assert.That(await page.GetByRole(AriaRole.Textbox, new() { Name = "Digit 1", Exact = true }).GetAttributeAsync("autocomplete")).IsEqualTo("one-time-code");
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Digit 1", Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Assert.That(await page.GetByRole(AriaRole.Textbox, new() { Name = "Digit 2", Exact = true }).EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            await page.Keyboard.PressAsync("ArrowLeft");
            await Assert.That(await page.GetByRole(AriaRole.Textbox, new() { Name = "Digit 1", Exact = true }).EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
        }
    }

    private static async Task FillMobileCodeAsync(IPage page, string id, string code)
    {
        await page.Locator(await page.Locator(".otp").CountAsync() > 0 ? ".otp input" : $"#{id}").First.FillAsync(code);
        await Assert.That(await page.Locator($"#{id}").InputValueAsync() == code).IsTrue();
    }

    private static async Task MobileErrorAsync(IPage page, string selector, string text)
    {
        await page.GetByRole(AriaRole.Alert).WaitForAsync();
        await Assert.That(await page.GetByRole(AriaRole.Alert).InnerTextAsync()).Contains(text);
        await Assert.That(await page.Locator(selector).GetAttributeAsync("aria-describedby")).Contains("form-error");
    }

    private static async Task MobileAuditAsync(IPage page)
    {
        await Assert.That(await page.GetByRole(AriaRole.Main).CountAsync()).IsEqualTo(1);
        await Assert.That(await page.GetByRole(AriaRole.Heading, new() { Level = 1 }).CountAsync()).IsEqualTo(1);
        await Assert.That(await page.Locator("#submission-status").GetAttributeAsync("aria-live")).IsEqualTo("polite");
        var viewport = page.ViewportSize!;
        await page.SetViewportSizeAsync(viewport.Width, 360);
        var control = page.Locator("input:not([type=hidden])").First;
        if (await control.CountAsync() > 0)
        {
            // Center explicitly: Chromium's minimal scroll can leave a fractional pixel outside the viewport.
            await control.EvaluateAsync("el => el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' })");
            var bounds = await control.EvaluateAsync<double[]>("el => { const r = el.getBoundingClientRect(); return [r.top, r.bottom, innerHeight]; }");
            await Assert.That(bounds[0] >= 0 && bounds[1] <= bounds[2]).IsTrue()
                .Because($"the first control must fit the reduced viewport: top={bounds[0]}, bottom={bounds[1]}, height={bounds[2]}");
        }
        await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
        await Assert.That(await page.EvaluateAsync<bool>("""
            () => {
              const forms = [...document.forms], fields = [...document.querySelectorAll('input:not([type=hidden])')];
              const unique = values => values.every(Boolean) && new Set(values).size === values.length;
              return unique(forms.map(f => f.id)) && unique(forms.map(f => f.name))
                && unique(fields.map(f => f.id)) && fields.every(f => f.name)
                && !document.querySelector('[autocomplete=off]')
                && document.documentElement.scrollWidth <= document.documentElement.clientWidth;
            }
            """)).IsTrue();
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "axe.min.js"));
        await page.EvaluateAsync(source);
        await Assert.That(await page.EvaluateAsync<bool>("window.axe.version === '4.14.0'")).IsTrue();
        foreach (var theme in new[] { "light", "dark" })
        {
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Theme", Exact = true }).SelectOptionAsync(theme);
            await ExpectSchemeAsync(page, theme);
            // Return rule IDs only: axe's HTML/node output can contain passwords or sealed handles.
            var violations = await page.EvaluateAsync<string[]>("async () => (await window.axe.run(document)).violations.map(v => v.id + ': ' + v.nodes.flatMap(n => n.target).join(', '))");
            await Assert.That(violations).IsEmpty();
        }
    }
}
