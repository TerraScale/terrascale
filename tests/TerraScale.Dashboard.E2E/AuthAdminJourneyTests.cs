using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using TUnit.Assertions.Enums;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class AuthAdminJourneyTests(DashboardE2EFixture fixture)
{
    private static readonly string[] AdminPages =
    [
        "/admin", "/admin/users", "/admin/organizations", "/admin/projects",
        "/admin/databases", "/admin/estate?tab=hosts&q=owned%20project",
        "/admin/usage", "/admin/plans", "/admin/pricing", "/admin/unknown"
    ];

    private static readonly string[] AdminActions =
    [
        "/api/v1/admin/users/foreign-account",
        "/api/v1/admin/organizations/foreign-account/foreign-org",
        "/api/v1/admin/projects/foreign-account/foreign-org/foreign-project",
        "/api/v1/admin/estate/action", "/api/v1/admin/plans",
        "/api/v1/admin/plans/foreign-plan/visibility",
        "/api/v1/admin/plans/foreign-plan/delete"
    ];

    private static readonly string[] AdminTools =
    [
        "admin_list_users", "admin_list_organizations", "admin_list_projects",
        "admin_list_databases", "admin_get_estate", "admin_get_usage",
        "admin_list_plans", "admin_update_user", "admin_rename_organization",
        "admin_rename_project", "admin_record_estate_action", "admin_save_plan",
        "admin_set_plan_visibility", "admin_delete_plan"
    ];

    [Test]
    public async Task DesktopAndMobileAuthOnboardingAndCliBrowserJourneys()
    {
        var output = BrowserAudit.Output(fixture.Root, "auth-journey");
        var origin = fixture.Origin.ToString().TrimEnd('/');
        var json = new JsonSerializerOptions { WriteIndented = true };
        var errors = new ConcurrentQueue<string>();
        var checks = new List<string>();
        IPage? active = null;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        try
        {
            await AutofocusAsync();
            foreach (var (size, width, height, paid) in new[] { ("desktop", 1440, 900, false), ("mobile", 390, 844, true) })
            {
                var (context, page) = await NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
                var key = Guid.NewGuid().ToString();
                var user = new BrowserUser("Browser " + size + " User", "browser-" + key + "@example.com", "signup-" + key);
                await SignupAsync(page, user, !paid, !paid);
                Console.WriteLine("PASS: " + size + " signup and verification");
                await OnboardingAsync(context, page, user, paid, size);
                Console.WriteLine("PASS: " + size + (paid ? " paid annual" : " Free") + " onboarding and persisted billing");
                if (!paid)
                {
                    await ResetAsync(context, page, user);
                    await CliAsync(page, user);
                    Console.WriteLine("PASS: reset and full CLI lifecycle");
                    var (_, native) = await NewContextAsync(new() { JavaScriptEnabled = false });
                    await Assert.That((await LoginAsync(native, user)).Status).IsEqualTo(303);
                    await native.WaitForURLAsync(origin + "/api/v1/auth/me");
                    await LogoutAsync(native, keyboard: true);
                    checks.Add("new-account login/logout work with JavaScript disabled");
                    Console.WriteLine("PASS: JavaScript-disabled native login and keyboard logout");
                }
                await LogoutAsync(page);
            }
            await SsoAsync();
            await Assert.That(errors).IsEmpty().Because("no browser script errors");
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed = true, @base = origin, suite = "all", checks }, json));
            Console.WriteLine("PASS: " + string.Join("; ", checks));
        }
        catch (Exception error)
        {
            if (active is { IsClosed: false })
            {
                try { await active.ScreenshotAsync(new() { Path = Path.Combine(output, "failure.png"), FullPage = true }); } catch { }
                try { await File.WriteAllTextAsync(Path.Combine(output, "failure.html"), await active.ContentAsync()); } catch { }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            { passed = false, @base = origin, suite = "all", url = active?.Url, checks, errors, error = error.Message }, json));
            throw;
        }

        async Task<(IBrowserContext Context, IPage Page)> NewContextAsync(BrowserNewContextOptions? options = null)
        {
            var context = await browser.NewContextAsync(options);
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(15000);
            page.PageError += (_, error) => errors.Enqueue(error);
            active = page;
            return (context, page);
        }
        async Task GotoAsync(IPage page, string path, int status = 200)
        {
            active = page;
            var response = await page.GotoAsync(origin + path, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(status).Because(path);
        }
        async Task<IResponse> SubmitAsync(IPage page, string form, string name, bool keyboard = false)
        {
            active = page;
            if (name.EndsWith(" →", StringComparison.Ordinal)) name = name[..^2];
            var action = await page.Locator(form).GetAttributeAsync("action");
            var button = page.Locator(form).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
            var response = page.WaitForResponseAsync(value => new Uri(value.Url).AbsolutePath == action && value.Request.Method == "POST");
            // Playwright marks waitForNavigation obsolete; the journey needs it so the POST's document has loaded before asserting.
#pragma warning disable CS0612
            var navigation = page.WaitForNavigationAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
#pragma warning restore CS0612
            await Task.WhenAll(response, navigation, keyboard ? button.PressAsync("Enter") : button.ClickAsync());
            return await response;
        }
        async Task ShotAsync(IPage page, string name)
        {
            await page.EvaluateAsync("() => document.fonts.ready");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide });
        }
        async Task AutofocusAsync()
        {
            var evidence = new List<object>();
            foreach (var (key, path, expected) in new[] { ("login", "/login", Array.Empty<string>()), ("code", "/login/code", ["email"]), ("reset", "/reset-password", []), ("login-rejected", "/login", ["password"]) })
            {
                var (context, page) = await NewContextAsync();
                var response = (await page.GotoAsync(origin + path))!;
                if (key == "login-rejected")
                {
                    await page.Locator("#email").FillAsync("unregistered-focus@example.com");
                    await page.Locator("#password").FillAsync("wrong-password");
                    response = await SubmitAsync(page, "form[action=\"/login\"]", "Sign in");
                    await Assert.That(response.Status).IsEqualTo(401);
                }
                var raw = Regex.Matches(await response.TextAsync(), "<input\\b[^>]*\\bautofocus(?:\\s|=|/|>)[^>]*>").Select(match => match.Value).ToArray();
                var rawIds = raw.Select(input => Regex.Match(input, "\\bid=\"([^\"]+)\"") is { Success: true } id ? id.Groups[1].Value : null).ToArray();
                await Assert.That(rawIds).IsEquivalentTo((string?[])expected, CollectionOrdering.Matching).Because(key + " raw HTML autofocus");
                await Assert.That(await page.Locator("input[autofocus]").EvaluateAllAsync<string[]>("inputs => inputs.map(input => input.id)"))
                    .IsEquivalentTo(expected, CollectionOrdering.Matching).Because(key + " DOM autofocus");
                await page.WaitForFunctionAsync("id => (document.activeElement?.id || document.activeElement?.tagName.toLowerCase()) === id", expected.FirstOrDefault() ?? "body");
                var activeElement = await page.EvaluateAsync<string>("() => document.activeElement.id || document.activeElement.tagName.toLowerCase()");
                evidence.Add(new { key, rawInputs = raw, autofocus = rawIds, activeElement });
                await context.CloseAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(output, "autofocus.json"), JsonSerializer.Serialize(evidence, json));
            checks.Add("raw HTML and browser focus match Go login/code/reset/rejected-login rules");
            Console.WriteLine("PASS: login/code/reset/rejected-login raw autofocus and activeElement");
        }
        async Task FillSignupAsync(IPage page, BrowserUser user, string? name = null, string? email = null, string? password = null, string? confirm = null, bool terms = true)
        {
            await page.Locator("#displayName").FillAsync(name ?? user.Name);
            await page.Locator("#email").FillAsync(email ?? user.Email);
            await page.Locator("#password").FillAsync(password ?? user.Password);
            await page.Locator("#confirmPassword").FillAsync(confirm ?? user.Password);
            await page.Locator("#terms").SetCheckedAsync(terms);
        }
        async Task SignupAsync(IPage page, BrowserUser user, bool validate, bool resend)
        {
            await GotoAsync(page, "/onboarding?plan=starter");
            await BrowserAudit.AssertAccessibleAsync(page, "signup accessibility");
            if (validate)
            {
                await FillSignupAsync(page, user, name: "");
                await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = false }).ClickAsync();
                await Assert.That(await page.Locator("#displayName").EvaluateAsync<bool>("input => input.validity.valueMissing")).IsTrue()
                    .Because("native required field blocks empty signup");
                foreach (var (name, email, password, confirm, terms, field, text) in new (string?, string?, string?, string?, bool, string, string)[]
                {
                    ("", null, null, null, true, "displayName", "Enter your full name."),
                    (null, "invalid", null, null, true, "email", "Enter a valid email address"),
                    (null, null, "short", "short", true, "password", "Use a password with at least 12 characters."),
                    (null, null, null, "different-password", true, "confirmPassword", "Passwords do not match."),
                    (null, null, null, null, false, "terms", "Accept the Terms of Service")
                })
                {
                    await FillSignupAsync(page, user, name, email, password, confirm, terms);
                    await page.Locator("form[action=\"/onboarding\"]").EvaluateAsync("form => { form.noValidate = true; }");
                    var rejected = await SubmitAsync(page, "form[action=\"/onboarding\"]", "Create account →");
                    await Assert.That(rejected.Status).IsEqualTo(422).Because(field + " rejects before provider enrollment");
                    await Assert.That(await page.Locator("#" + field + "-error").InnerTextAsync()).Contains(text);
                    await Assert.That(await page.Locator("#" + field).GetAttributeAsync("aria-invalid")).IsEqualTo("true");
                    await Assert.That(await page.Locator("#password").InputValueAsync()).IsEqualTo("").Because("rejected signup does not echo password");
                }
                checks.Add("native signup validation and all five server field errors");
            }
            await FillSignupAsync(page, user);
            await Assert.That((await SubmitAsync(page, "form[action=\"/onboarding\"]", "Create account →")).Status).IsEqualTo(200);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Verify your email", Exact = true }).WaitForAsync();
            await Assert.That(await page.Locator("input[name=email]").InputValueAsync()).IsEqualTo(user.Email);
            await Assert.That(await page.Locator("form[action=\"/login/code/verify\"] input[name=signUp]").InputValueAsync()).IsEqualTo("1");
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Six-digit code", Exact = true }).FillAsync("000000");
            await Assert.That((await SubmitAsync(page, "form[action=\"/login/code/verify\"]", "Continue")).Status).IsEqualTo(401);
            await page.Locator("#code-error").WaitForAsync();
            await BrowserAudit.AssertAccessibleAsync(page, "rejected signup verification accessibility");
            if (resend)
            {
                var button = page.Locator("form[action=\"/login/code/resend\"] button");
                await Assert.That(await button.IsDisabledAsync()).IsTrue().Because("resend countdown is enforced by native button state");
                var csrf = await page.Locator("form[action=\"/login/code/resend\"] input[name=_csrf]").InputValueAsync();
                var form = page.APIRequest.CreateFormData();
                form.Set("_csrf", csrf);
                var tooSoon = await page.APIRequest.PostAsync(origin + "/login/code/resend", new() { Form = form });
                await Assert.That(tooSoon.Status).IsEqualTo(429).Because("server also refuses early resend");
                await Assert.That(int.TryParse(tooSoon.Headers.GetValueOrDefault("retry-after"), out var retryAfter) && retryAfter > 0).IsTrue();
                Console.WriteLine("Waiting for the real signup resend cooldown…");
                await button.WaitForAsync(new() { State = WaitForSelectorState.Visible });
                await page.WaitForFunctionAsync("() => !document.querySelector('form[action=\"/login/code/resend\"] button').disabled", null, new() { Timeout = 65000 });
                await Assert.That((await SubmitAsync(page, "form[action=\"/login/code/resend\"]", "Resend code")).Status).IsEqualTo(200);
                await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Check your email for a new code." }).WaitForAsync();
                checks.Add("signup resend countdown, server cooldown and successful resend");
            }
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Six-digit code", Exact = true }).FillAsync("123456");
            await Assert.That((await SubmitAsync(page, "form[action=\"/login/code/verify\"]", "Continue")).Status).IsEqualTo(303);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Review your account information", Exact = true }).WaitForAsync();
            await Assert.That(await page.Locator("#displayName").InputValueAsync()).IsEqualTo(user.Name).Because("registered name is persisted after verification");
            await Assert.That(await page.Locator("#accountEmail").InputValueAsync()).IsEqualTo(user.Email);
            checks.Add("signup wrong-code rejection and successful verification");
        }
        async Task OnboardingAsync(IBrowserContext context, IPage page, BrowserUser user, bool paid, string size)
        {
            user.Name += " Updated";
            await page.Locator("#displayName").FillAsync(user.Name);
            await SubmitAsync(page, "form[action=\"/onboarding/step\"]", "Save and continue →");
            await page.GetByRole(AriaRole.Heading, new() { Name = "Choose your plan", Exact = true }).WaitForAsync();
            await GotoAsync(page, "/onboarding");
            await page.GetByRole(AriaRole.Heading, new() { Name = "Choose your plan", Exact = true }).WaitForAsync();
            await BrowserAudit.AssertAccessibleAsync(page, "onboarding plans accessibility");
            if (paid)
            {
                await GotoAsync(page, "/onboarding?payment=success&plan=starter");
                await page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Payment setup could not be confirmed." }).WaitForAsync();
                await GotoAsync(page, "/onboarding?payment=canceled&plan=starter");
                await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Payment setup canceled" }).WaitForAsync();
                await Assert.That(await page.Locator("input[name=plan][value=starter]").IsCheckedAsync()).IsTrue().Because("cancel keeps selected plan");
                await page.Locator(".ts-segmented label").Filter(new() { HasText = "Annual" }).ClickAsync();
                await Assert.That(await page.Locator("input[name=interval][value=annual]").IsCheckedAsync()).IsTrue();
                await page.Locator("label.ts-onboarding-plan").Filter(new() { Has = page.Locator("input[name=plan][value=pro]") }).ClickAsync();
                await Assert.That(await page.Locator("input[name=plan][value=pro]").IsCheckedAsync()).IsTrue();
                await page.Locator("label.ts-onboarding-plan").Filter(new() { Has = page.Locator("input[name=plan][value=starter]") }).ClickAsync();
                await Assert.That(await page.Locator("input[name=plan][value=starter]").IsCheckedAsync()).IsTrue();
                var checkout = await SubmitAsync(page, "form[action=\"/onboarding/step\"]", "Continue to checkout →");
                await Assert.That(checkout.Status).IsEqualTo(303);
                await Assert.That(Regex.IsMatch(checkout.Headers.GetValueOrDefault("location") ?? "", "/__test/checkout\\?id=cs_")).IsTrue()
                    .Because("official Stripe SDK uses test-only provider checkout");
                checks.Add("paid annual checkout, unconfirmed payment rejection and cancellation recovery");
            }
            else
            {
                await SubmitAsync(page, "form[action=\"/onboarding/step\"]", "Continue on Free");
                checks.Add("free personal onboarding bypass");
            }
            await page.GetByRole(AriaRole.Heading, new() { Name = "Create your first project", Exact = true }).WaitForAsync();
            await BrowserAudit.AssertAccessibleAsync(page, "onboarding project accessibility");
            await ShotAsync(page, "project-" + size);
            await page.Locator("#projectName").FillAsync("Browser " + size + " Project");
            if (!paid)
            {
                await page.Locator("#region").EvaluateAsync("select => { select.add(new Option('Invalid', 'MARS')); select.value = 'MARS'; }");
                await SubmitAsync(page, "form[action=\"/onboarding/step\"]", "Create project and open Dashboard →");
                await page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Choose a supported project region." }).WaitForAsync();
                await page.Locator("#projectName").FillAsync("Browser " + size + " Project");
                checks.Add("unsupported project region is rejected without advancing onboarding");
            }
            await page.Locator("#region").SelectOptionAsync(paid ? "SFO" : "GRU");
            await SubmitAsync(page, "form[action=\"/onboarding/step\"]", "Create project and open Dashboard →");
            await page.WaitForURLAsync(origin + "/");
            var me = (await (await context.APIRequest.GetAsync(origin + "/api/v1/auth/me")).JsonAsync())!.Value;
            await Assert.That(me.GetProperty("email").GetString()).IsEqualTo(user.Email);
            await Assert.That(me.GetProperty("name").GetString()).IsEqualTo(user.Name);
            await Assert.That(me.GetProperty("onboardingCompleted").GetBoolean()).IsTrue();
            await Assert.That(!string.IsNullOrEmpty(me.GetProperty("organizationId").GetString()) && !string.IsNullOrEmpty(me.GetProperty("projectId").GetString()) &&
                me.GetProperty("role").GetString() == "owner").IsTrue();
            await GotoAsync(page, "/onboarding");
            await Assert.That(page.Url).IsEqualTo(origin + "/").Because("completed onboarding redirects to dashboard");
            await ShotAsync(page, "complete-" + size);
            await GotoAsync(page, "/organization?tab=billing");
            var summary = await page.Locator("#billing-summary").InnerTextAsync();
            await Assert.That(summary).Contains(paid ? "Starter" : "Free").Because("confirmed plan is persisted");
            if (paid) await Assert.That(summary).Contains("On file").Because("confirmed Stripe subscription stores payment status");
            if (paid) await Assert.That(summary).Contains("Annual").Because("confirmed subscription retains selected annual interval");
            checks.Add(size + " completed account/project persistence and billing");
        }
        async Task<IResponse> LoginAsync(IPage page, BrowserUser user, string? password = null)
        {
            await GotoAsync(page, "/login?returnTo=%2Fapi%2Fv1%2Fauth%2Fme");
            await page.Locator("#email").FillAsync(user.Email);
            await page.Locator("#password").FillAsync(password ?? user.Password);
            return await SubmitAsync(page, "form[action=\"/login\"]", "Sign in");
        }
        async Task LogoutAsync(IPage page, bool keyboard = false)
        {
            await GotoAsync(page, "/");
            if (!await page.Locator("details.ts-account summary").IsVisibleAsync())
                await page.GetByRole(AriaRole.Button, new() { Name = "Open navigation", Exact = true }).ClickAsync();
            await page.Locator("details.ts-account summary").ClickAsync();
            await Assert.That((await SubmitAsync(page, "form[action=\"/api/v1/auth/logout\"]", "Sign out", keyboard)).Status).IsEqualTo(303);
            await Assert.That(new Uri(page.Url).AbsolutePath).IsEqualTo("/login");
            await Assert.That((await page.APIRequest.GetAsync(origin + "/api/v1/auth/me")).Status).IsEqualTo(401);
        }
        async Task ResetAsync(IBrowserContext context, IPage page, BrowserUser user)
        {
            var old = user.Password;
            var changed = "reset-" + Guid.NewGuid();
            await GotoAsync(page, "/reset-password");
            await page.Locator("#email").FillAsync(user.Email);
            await SubmitAsync(page, "form[action=\"/reset-password\"]", "Email me a code");
            foreach (var (code, confirm, status, error) in new[] { ("000000", changed, 401, "#code-error"), ("123456", "mismatching-password", 422, "#confirmPassword-error"), ("123456", changed, 303, "") })
            {
                await page.Locator("#code").FillAsync(code);
                await page.Locator("#password").FillAsync(changed);
                await page.Locator("#confirmPassword").FillAsync(confirm);
                await Assert.That((await SubmitAsync(page, "form[action=\"/reset-password/complete\"]", "Change password")).Status).IsEqualTo(status);
                if (error.Length > 0) await page.Locator(error).WaitForAsync();
            }
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Your password was changed." }).WaitForAsync();
            await Assert.That((await context.APIRequest.GetAsync(origin + "/api/v1/auth/me")).Status).IsEqualTo(401).Because("password reset invalidates earlier browser grant");
            await Assert.That((await LoginAsync(page, user, old)).Status).IsEqualTo(401).Because("old password cannot sign in");
            user.Password = changed;
            await Assert.That((await LoginAsync(page, user)).Status).IsEqualTo(303);
            await page.WaitForURLAsync(origin + "/api/v1/auth/me");
            checks.Add("reset wrong-code/mismatch rejection, success, old-grant invalidation and new-password login");
        }
        async Task CliAsync(IPage page, BrowserUser user)
        {
            var publicContext = await browser.NewContextAsync();
            var client = publicContext.APIRequest;
            async Task<JsonElement> IssueAsync()
            {
                var form = client.CreateFormData();
                form.Set("client_id", "terrascale-cli");
                var response = await client.PostAsync(origin + "/api/v1/dashboard/cli/device-authorization", new() { Form = form });
                await Assert.That(response.Status).IsEqualTo(200);
                var issued = (await response.JsonAsync())!.Value;
                await Assert.That(issued.GetProperty("expires_in").GetInt32()).IsEqualTo(600);
                await Assert.That(issued.GetProperty("interval").GetInt32()).IsEqualTo(5);
                await Assert.That(issued.GetProperty("verification_uri").GetString()).IsEqualTo(origin + "/cli/approve");
                return issued;
            }
            Task<IAPIResponse> PollAsync(JsonElement grant)
            {
                var form = client.CreateFormData();
                form.Set("client_id", "terrascale-cli");
                form.Set("grant_type", "urn:ietf:params:oauth:grant-type:device_code");
                form.Set("device_code", grant.GetProperty("device_code").GetString()!);
                return client.PostAsync(origin + "/api/v1/dashboard/cli/token", new() { Form = form });
            }
            static async Task<string?> ErrorAsync(IAPIResponse response) => (await response.JsonAsync())!.Value.GetProperty("error").GetString();
            var grant = await IssueAsync();
            var tooSoon = await PollAsync(grant);
            await Assert.That(tooSoon.Status).IsEqualTo(400);
            await Assert.That(await ErrorAsync(tooSoon)).IsEqualTo("slow_down");
            var anonymous = await publicContext.NewPageAsync();
            await anonymous.GotoAsync(grant.GetProperty("verification_uri_complete").GetString()!);
            await anonymous.GetByRole(AriaRole.Link, new() { Name = "Sign in to approve", Exact = true }).WaitForAsync();
            await GotoAsync(page, "/cli/approve?user_code=" + grant.GetProperty("user_code").GetString());
            await page.Locator("#cliPassword").FillAsync("wrong-password");
            await SubmitAsync(page, "form[action=\"/cli/approve/submit\"]", "Authorize CLI");
            await page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Current password is incorrect." }).WaitForAsync();
            await page.Locator("#cliPassword").FillAsync(user.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true }).ClickAsync();
            await Assert.That(await page.Locator("#cliPassword").GetAttributeAsync("type")).IsEqualTo("text");
            await page.GetByRole(AriaRole.Button, new() { Name = "Hide password", Exact = true }).ClickAsync();
            await SubmitAsync(page, "form[action=\"/cli/approve/submit\"]", "Authorize CLI");
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "CLI authorized" }).WaitForAsync();
            await ShotAsync(page, "cli-authorized");
            await page.WaitForTimeoutAsync(11000);
            var polls = await Task.WhenAll(PollAsync(grant), PollAsync(grant));
            await Assert.That(polls.Count(response => response.Status == 200)).IsEqualTo(1).Because("concurrent public polls return one token");
            var delivered = (await polls.First(response => response.Status == 200).JsonAsync())!.Value;
            var token = delivered.GetProperty("access_token").GetString()!;
            await Assert.That(Regex.IsMatch(token, "^tspat_[A-Za-z0-9_-]{43}$")).IsTrue();
            await Assert.That(delivered.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
            Task<IAPIResponse> McpAsync() => client.PostAsync(origin + "/mcp", new()
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["Accept"] = "application/json, text/event-stream" },
                DataObject = new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } }
            });
            await Assert.That((await McpAsync()).Status).IsEqualTo(200);
            var replay = await PollAsync(grant);
            await Assert.That(replay.Status).IsEqualTo(400);
            await Assert.That(await ErrorAsync(replay)).IsEqualTo("invalid_grant");
            await LogoutAsync(page);
            await Assert.That((await McpAsync()).Status).IsEqualTo(200).Because("independent CLI grant survives browser logout");
            await Assert.That((await LoginAsync(page, user)).Status).IsEqualTo(303);
            grant = await IssueAsync();
            await GotoAsync(page, "/cli/approve?user_code=" + grant.GetProperty("user_code").GetString());
            await SubmitAsync(page, "form[action=\"/cli/approve/submit\"]", "Deny request");
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Request denied" }).WaitForAsync();
            await page.WaitForTimeoutAsync(5300);
            await Assert.That(await ErrorAsync(await PollAsync(grant))).IsEqualTo("access_denied");
            grant = await IssueAsync();
            await GotoAsync(page, "/cli/approve?user_code=" + grant.GetProperty("user_code").GetString());
            await page.Locator("#cliPassword").FillAsync(user.Password);
            await SubmitAsync(page, "form[action=\"/cli/approve/submit\"]", "Authorize CLI");
            await LogoutAsync(page);
            await page.WaitForTimeoutAsync(5300);
            await Assert.That(await ErrorAsync(await PollAsync(grant))).IsEqualTo("access_denied").Because("browser logout before redemption invalidates approval");
            await Assert.That((await LoginAsync(page, user)).Status).IsEqualTo(303);
            await GotoAsync(page, "/profile?tab=access-tokens");
            var row = page.Locator("#profile-access-tokens tbody tr").Filter(new() { HasText = "TerraScale CLI" });
            await Assert.That(await row.CountAsync()).IsEqualTo(1).Because("only successful single-use flow created a token");
            await row.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
            await SubmitAsync(page, "dialog[open] form", "Revoke token");
            await Assert.That((await McpAsync()).Status).IsEqualTo(401).Because("native profile revocation invalidates CLI token");
            checks.Add("CLI anonymous/reauth/password-toggle approval, slow poll, single use, denial, logout binding, MCP and revocation");
        }
        async Task SsoAsync()
        {
            var (context, page) = await NewContextAsync();
            await GotoAsync(page, "/login/callback?state=forged&code=test-code", 400);
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "This sign-in link expired or was started in another browser." }).WaitForAsync();
            await BrowserAudit.AssertAccessibleAsync(page, "SSO recovery accessibility");
            await page.GetByRole(AriaRole.Link, new() { Name = "Try again", Exact = true }).ClickAsync();
            await Assert.That(QueryHelpers.ParseQuery(new Uri(page.Url).Query).ContainsKey("prompt")).IsFalse().Because("ts-auth answers login_required to prompt=login");
            await GotoAsync(page, "/login?returnTo=%2Fapi%2Fv1%2Fauth%2Fme");
            var callbackRequest = page.WaitForRequestAsync(request => new Uri(request.Url).AbsolutePath == "/login/callback" &&
                QueryHelpers.ParseQuery(new Uri(request.Url).Query).GetValueOrDefault("code") == "test-code");
            await page.GetByRole(AriaRole.Link, new() { Name = "Single sign-on", Exact = true }).ClickAsync();
            var callbackUrl = new Uri((await callbackRequest).Url);
            await Assert.That(Regex.IsMatch(QueryHelpers.ParseQuery(callbackUrl.Query).GetValueOrDefault("state").ToString(), "^[a-f0-9]{64}$")).IsTrue()
                .Because("test-only OIDC producer returns the issued state");
            await page.WaitForURLAsync(origin + "/api/v1/auth/me");
            await Assert.That((await context.APIRequest.GetAsync(origin + "/api/v1/auth/me")).Status).IsEqualTo(200);
            await GotoAsync(page, callbackUrl.PathAndQuery, 400);
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "This sign-in link expired or was started in another browser." }).WaitForAsync();
            await LogoutAsync(page);
            checks.Add("SSO browser recovery, test-only producer callback, state verification and single-use callback");
        }
    }

    [Test]
    public async Task NativeAuthenticationAndAdminAuthorizationAcrossRunningReplicas()
    {
        using var client = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);

        foreach (var path in AdminPages)
        {
            using var anonymous = await client.GetAsync(path);
            Check(anonymous.StatusCode == HttpStatusCode.SeeOther &&
                anonymous.Headers.Location?.OriginalString == "/login?returnTo=" + Uri.EscapeDataString(path),
                "Anonymous admin GET preserves the complete local return path: " + path);
        }
        foreach (var path in AdminActions)
        {
            using var anonymous = await client.PostAsync(path, new StringContent("untrusted form"));
            Check(anonymous.StatusCode == HttpStatusCode.SeeOther && anonymous.Headers.Location?.OriginalString == "/login",
                "Anonymous admin POST returns to login before parsing: " + path);
        }
        using (var anonymousHead = await client.SendAsync(new(HttpMethod.Head, "/admin/users")))
            Check(anonymousHead.StatusCode == HttpStatusCode.SeeOther && anonymousHead.Headers.Location?.OriginalString == "/login",
                "Anonymous HEAD matches the original login redirect");

        using var loginPage = await client.GetAsync("/login?returnTo=%2Fadmin%2Fusers");
        Check(loginPage.StatusCode == HttpStatusCode.OK, "Native login form is available");
        var csrf = Hidden(await loginPage.Content.ReadAsStringAsync(), "_csrf");
        Check(csrf.Length == 64 && loginPage.Headers.CacheControl?.NoStore == true, "Auth form supplies CSRF and prevents caching");
        var credentials = new Dictionary<string, string>
        {
            ["email"] = "user@example.com",
            ["password"] = "test-password",
            ["redirectTo"] = "/admin/users"
        };
        using (var missing = await client.PostAsync("/login", new FormUrlEncodedContent(credentials)))
            Check(missing.StatusCode == HttpStatusCode.Forbidden, "A missing form token cannot sign in");
        credentials["_csrf"] = (csrf[0] == 'a' ? "b" : "a") + csrf[1..];
        using (var forged = await client.PostAsync("/login", new FormUrlEncodedContent(credentials)))
            Check(forged.StatusCode == HttpStatusCode.Forbidden, "A forged form token cannot sign in");
        credentials["_csrf"] = csrf;
        using (var foreignRequest = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent(credentials) })
        {
            foreignRequest.Headers.Add("Origin", "https://untrusted.example");
            using var foreign = await client.SendAsync(foreignRequest);
            Check(foreign.StatusCode == HttpStatusCode.Forbidden, "Foreign Origin cannot sign in using a valid token");
        }
        using var signedIn = await client.PostAsync("/login", new FormUrlEncodedContent(credentials));
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther && signedIn.Headers.Location?.OriginalString == "/admin/users",
            "Native login preserves a local admin return path");
        var sessionCookie = SessionCookie(signedIn);
        Check(signedIn.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal) &&
            value.Contains("httponly", StringComparison.OrdinalIgnoreCase)), "Browser session is HttpOnly");

        using (var current = await client.GetAsync("/api/v1/auth/me"))
        using (var replicated = await Request(replica, HttpMethod.Get, "/api/v1/auth/me", sessionCookie))
        {
            Check(current.StatusCode == HttpStatusCode.OK && replicated.StatusCode == HttpStatusCode.OK,
                "The issued browser cookie authenticates on both running replicas");
            using var first = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
            using var second = JsonDocument.Parse(await replicated.Content.ReadAsStringAsync());
            Check(first.RootElement.GetProperty("userId").GetString() == second.RootElement.GetProperty("userId").GetString() &&
                first.RootElement.GetProperty("accountId").GetString() == second.RootElement.GetProperty("accountId").GetString() &&
                first.RootElement.GetProperty("role").GetString() == "owner", "Replicas resolve the same persisted ordinary owner");
        }

        foreach (var server in new[] { client, replica })
        {
            foreach (var path in AdminPages)
                await Denied(server, HttpMethod.Get, path, sessionCookie);
            foreach (var path in AdminActions)
                await Denied(server, HttpMethod.Post, path, sessionCookie);
            using var deniedHead = await Request(server, HttpMethod.Head, "/admin/users", sessionCookie);
            Check(deniedHead.StatusCode == HttpStatusCode.Forbidden, "HEAD cannot bypass platform authorization");
        }
        using (var unknown = await client.PostAsync("/api/v1/admin/unmapped", new StringContent("{}")))
            Check(unknown.StatusCode == HttpStatusCode.NotFound, "Unknown native administration APIs remain unmapped");

        using var tokenPage = await client.GetAsync("/profile?tab=access-tokens");
        Check(tokenPage.StatusCode == HttpStatusCode.OK, "Native personal access token form is available");
        var tokenCsrf = Hidden(await tokenPage.Content.ReadAsStringAsync(), "_csrf");
        var tokenName = "TUnit ordinary-owner " + Guid.NewGuid().ToString("N");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/access-tokens", new Dictionary<string, string>
        {
            ["_csrf"] = tokenCsrf,
            ["name"] = tokenName,
            ["expiresInDays"] = "30",
            ["password"] = "test-password"
        });
        Check(created.StatusCode == HttpStatusCode.OK, "A fresh password grant creates the PAT through its native form");
        var createdHtml = await created.Content.ReadAsStringAsync();
        var secret = InputById(createdHtml, "access-token-secret");
        Check(Regex.IsMatch(secret, "^tspat_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant), "PAT plaintext is displayed after creation");
        var tokenRow = Regex.Matches(createdHtml, "<tr\\b[^>]*>.*?</tr>", RegexOptions.CultureInvariant | RegexOptions.Singleline)
            .Single(row => row.Value.Contains(tokenName, StringComparison.Ordinal)).Value;
        var revokePath = Regex.Match(tokenRow, "action=\"(/api/v1/dashboard/profile/access-tokens/[^\"/]+/revoke)\"", RegexOptions.CultureInvariant).Groups[1].Value;
        Check(revokePath.Length > 0, "The new token has a native revoke form");

        using (var listing = await Mcp(replica, "tools/list", new { }, secret))
        {
            var result = await McpJson(listing);
            var names = result.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Check(names.Length > 0 && !names.Intersect(AdminTools).Any(), "Ordinary owners see no platform administration tools");
        }
        foreach (var name in AdminTools)
        {
            using var unknown = await Mcp(replica, "tools/call", new
            {
                name,
                arguments = new { platformAdmin = true, accountId = "foreign-account", organizationId = "foreign-org" }
            }, secret);
            var result = await McpJson(unknown);
            Check(result.GetProperty("error").GetProperty("code").GetInt32() == -32602,
                "Unlisted administration tools cannot dispatch: " + name);
        }
        using (var validation = await Mcp(replica, "tools/call", new { name = "set_language", arguments = new { locale = "invalid-language" } }, secret))
        {
            var result = await McpJson(validation);
            Check(result.GetProperty("result").GetProperty("isError").GetBoolean() &&
                result.GetProperty("result").GetProperty("content").EnumerateArray().Any(content => content.GetProperty("text").GetString() == "Choose an available language."),
                "The real MCP pipeline preserves safe validation errors");
        }

        using (var logoutMissing = await client.PostAsync("/api/v1/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>())))
            Check(logoutMissing.StatusCode == HttpStatusCode.Forbidden, "Logout rejects a missing CSRF token");
        using (var stillSignedIn = await Request(replica, HttpMethod.Get, "/api/v1/auth/me", sessionCookie))
            Check(stillSignedIn.StatusCode == HttpStatusCode.OK, "Rejected logout does not invalidate the browser session");
        using (var logout = await client.PostAsync("/api/v1/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = tokenCsrf })))
        {
            Check(logout.StatusCode == HttpStatusCode.SeeOther && logout.Headers.Location?.OriginalString == "/login", "Native logout returns to login");
            Check(logout.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("ts-dashboard-session=;", StringComparison.Ordinal)), "Native logout clears the browser cookie");
        }
        using (var ended = await Request(replica, HttpMethod.Get, "/api/v1/auth/me", sessionCookie))
            Check(ended.StatusCode == HttpStatusCode.Unauthorized, "Logout invalidates a captured browser cookie on another replica");
        using (var endedAdmin = await Request(replica, HttpMethod.Get, "/admin/users", sessionCookie))
            Check(endedAdmin.StatusCode == HttpStatusCode.SeeOther && endedAdmin.Headers.Location?.OriginalString == "/login?returnTo=%2Fadmin%2Fusers",
                "The ended browser returns to sign-in on admin routes");
        using (var independent = await Mcp(replica, "tools/list", new { }, secret))
            Check(independent.StatusCode == HttpStatusCode.OK, "The independent PAT family survives browser logout");

        using var freshLoginPage = await client.GetAsync("/login");
        credentials["_csrf"] = Hidden(await freshLoginPage.Content.ReadAsStringAsync(), "_csrf");
        credentials["redirectTo"] = "https://untrusted.example/";
        using (var freshLogin = await client.PostAsync("/login", new FormUrlEncodedContent(credentials)))
            Check(freshLogin.StatusCode == HttpStatusCode.SeeOther && freshLogin.Headers.Location?.OriginalString == "/",
                "A successful login cannot redirect to an external origin");
        using var freshTokenPage = await client.GetAsync("/profile?tab=access-tokens");
        var freshHtml = await freshTokenPage.Content.ReadAsStringAsync();
        Check(!freshHtml.Contains(secret, StringComparison.Ordinal), "Reload never redisplays token plaintext");
        using (var revoked = await client.PostAsync(revokePath, new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = Hidden(freshHtml, "_csrf") })))
            Check(revoked.StatusCode == HttpStatusCode.SeeOther, "Native token revocation succeeds");
        using (var revoked = await Mcp(replica, "tools/list", new { }, secret))
            Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "A revoked PAT cannot reach MCP on another replica");
        using (var cleanupLogout = await HttpJourney.PostFormAsync(client, "/api/v1/auth/logout", new() { ["_csrf"] = Hidden(freshHtml, "_csrf") }))
            Check(cleanupLogout.StatusCode == HttpStatusCode.SeeOther, "The fresh browser grant is ended after the journey");
    }

    private static async Task Denied(HttpClient client, HttpMethod method, string path, string cookie)
    {
        using var response = await Request(client, method, path, cookie);
        Check(response.StatusCode == HttpStatusCode.Forbidden && await response.Content.ReadAsStringAsync() == "forbidden\n" &&
            response.Content.Headers.ContentType?.MediaType == "text/plain", "Ordinary owner cannot access administration: " + path);
    }

    private static Task<HttpResponseMessage> Request(HttpClient client, HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-Platform-Admin", "true");
        request.Headers.Add("X-Role", "platform-admin");
        if (method == HttpMethod.Post) request.Content = new StringContent("invalid form: platform_admin=true");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Mcp(HttpClient client, string method, object parameters, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> McpJson(HttpResponseMessage response)
    {
        Check(response.StatusCode == HttpStatusCode.OK, "Authenticated MCP request succeeds");
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join('\n', body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart()));
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    private static string Hidden(string html, string name)
    {
        var input = Regex.Match(html, "<input\\b[^>]*\\bname=\"" + Regex.Escape(name) + "\"[^>]*>", RegexOptions.CultureInvariant).Value;
        var value = WebUtility.HtmlDecode(Regex.Match(input, "\\bvalue=\"([^\"]*)\"", RegexOptions.CultureInvariant).Groups[1].Value);
        Check(value.Length > 0, "The rendered native form supplies " + name);
        return value;
    }

    private static string InputById(string html, string id)
    {
        var input = Regex.Match(html, "<input\\b[^>]*\\bid=\"" + Regex.Escape(id) + "\"[^>]*>", RegexOptions.CultureInvariant).Value;
        return WebUtility.HtmlDecode(Regex.Match(input, "\\bvalue=\"([^\"]*)\"", RegexOptions.CultureInvariant).Groups[1].Value);
    }

    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BrowserUser(string name, string email, string password)
    {
        public string Name { get; set; } = name;
        public string Email { get; } = email;
        public string Password { get; set; } = password;
    }
}
