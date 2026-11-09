using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
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
        public async Task DesktopAndMobileHostedSignUpOnboardingAndLogoutBrowserJourneys()
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
                foreach (var (size, width, height, paid) in new[] { ("desktop", 1440, 900, false), ("mobile", 390, 844, true) })
                {
                    var (context, page) = await NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
                    var key = Guid.NewGuid().ToString();
                    var user = new BrowserUser("Browser " + size + " User", "browser-" + key + "@example.com", "signup-" + key);
                    await SignupAsync(page, user);
                    Console.WriteLine("PASS: " + size + " hosted sign-up");
                    await OnboardingAsync(context, page, user, paid, size);
                    Console.WriteLine("PASS: " + size + (paid ? " paid annual" : " Free") + " onboarding and persisted billing");
                    if (!paid)
                    {
                        var (_, native) = await NewContextAsync(new() { JavaScriptEnabled = false });
                        var rejected = await BrowserAudit.SignInAsync(native, fixture.Origin, "/api/v1/auth/me", user.Email, "wrong-password");
                        await Assert.That(rejected!.Status).IsEqualTo(401).Because("ts-auth refusing the sign-in renders the failure page");
                        await native.GetByRole(AriaRole.Status).Filter(new() { HasText = "ts-auth did not sign you in." }).WaitForAsync();
                        await Assert.That((await native.APIRequest.GetAsync(origin + "/api/v1/auth/me")).Status).IsEqualTo(401);
                        await Assert.That((await BrowserAudit.SignInAsync(native, fixture.Origin, "/api/v1/auth/me", user.Email, user.Password))!.Status).IsEqualTo(200);
                        await Assert.That(native.Url).IsEqualTo(origin + "/api/v1/auth/me");
                        await LogoutAsync(native, keyboard: true);
                        checks.Add("rejected hosted sign-in, new-account sign-in and logout work with JavaScript disabled");
                        Console.WriteLine("PASS: JavaScript-disabled hosted sign-in and keyboard logout");
                    }
                    await LogoutAsync(page);
                }
                await FailedCallbackAsync();
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
                options ??= new();
                options.IgnoreHTTPSErrors = true;
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
                if (name.EndsWith(" →", StringComparison.Ordinal))
                {
                    name = name[..^2];
                }

                var action = new Uri(new Uri(origin), await page.Locator(form).GetAttributeAsync("action")).AbsolutePath;
                var button = page.Locator(form).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
                var response = page.WaitForResponseAsync(value => new Uri(value.Url).AbsolutePath == action && value.Request.Method == "POST");
                // Playwright marks waitForNavigation obsolete; the journey needs it so the POST's page has loaded before asserting.
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
            // ts-auth's hosted page creates the account; the dashboard continues to account setup.
            async Task SignupAsync(IPage page, BrowserUser user)
            {
                active = page;
                var landed = await BrowserAudit.SignInAsync(page, fixture.Origin, "/onboarding?plan=starter", user.Email, user.Password, signUp: true);
                await Assert.That(landed!.Status).IsEqualTo(200);
                await page.GetByRole(AriaRole.Heading, new() { Name = "Review your account information", Exact = true }).WaitForAsync();
                await Assert.That(await page.Locator("#accountEmail").InputValueAsync()).IsEqualTo(user.Email);
                await BrowserAudit.AssertAccessibleAsync(page, "account review accessibility");
                checks.Add("hosted sign-up lands on account review with the signed-up email");
            }
            async Task OnboardingAsync(IBrowserContext context, IPage page, BrowserUser user, bool paid, string size)
            {
                user.Name += " Updated";
                await page.Locator("#displayName").FillAsync(user.Name);
                await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Save and continue →");
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
                    await Assert.That(await page.Locator("label.ts-onboarding-plan").Filter(new() { Has = page.Locator("input[name=plan][value=starter]") }).InnerTextAsync()).Matches(@"\$3\.00\s+/ month");
                    await page.Locator("label.ts-onboarding-plan").Filter(new() { Has = page.Locator("input[name=plan][value=pro]") }).ClickAsync();
                    await Assert.That(await page.Locator("input[name=plan][value=pro]").IsCheckedAsync()).IsTrue();
                    await page.Locator("label.ts-onboarding-plan").Filter(new() { Has = page.Locator("input[name=plan][value=starter]") }).ClickAsync();
                    await Assert.That(await page.Locator("input[name=plan][value=starter]").IsCheckedAsync()).IsTrue();
                    var checkout = await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Continue to checkout →");
                    await Assert.That(checkout.Status).IsEqualTo(303);
                    await Assert.That(Regex.IsMatch(checkout.Headers.GetValueOrDefault("location") ?? "", "/__test/checkout\\?id=cs_")).IsTrue()
                        .Because("official Stripe SDK uses test-only provider checkout");
                    checks.Add("paid annual checkout, unconfirmed payment rejection and cancellation recovery");
                }
                else
                {
                    await Assert.That(await page.Locator("input[name=plan][value=free]").IsCheckedAsync()).IsTrue().Because("Free is the default selection");
                    await Assert.That(await page.Locator("label.ts-onboarding-plan").Filter(new() { Has = page.Locator("input[name=plan][value=free]") }).InnerTextAsync()).Contains("No card required");
                    await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Continue on Free");
                    checks.Add("free personal onboarding through the default Free card");
                }
                await page.GetByRole(AriaRole.Heading, new() { Name = "Create your first project", Exact = true }).WaitForAsync();
                await BrowserAudit.AssertAccessibleAsync(page, "onboarding project accessibility");
                await ShotAsync(page, "project-" + size);
                await page.Locator("#projectName").FillAsync("Browser " + size + " Project");
                if (!paid)
                {
                    await page.Locator("#region").EvaluateAsync("select => { select.add(new Option('Invalid', 'MARS')); select.value = 'MARS'; }");
                    await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Create project and open Dashboard →");
                    await page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Choose a supported project region." }).WaitForAsync();
                    await page.Locator("#projectName").FillAsync("Browser " + size + " Project");
                    checks.Add("unsupported project region is rejected without advancing onboarding");
                }
                await page.Locator("#region").SelectOptionAsync(paid ? "SJC" : "GRU");
                await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Create project and open Dashboard →");
                await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == origin + "/");
                var me = (await (await context.APIRequest.GetAsync(origin + "/api/v1/auth/me")).JsonAsync())!.Value;
                await Assert.That(me.GetProperty("email").GetString()).IsEqualTo(user.Email);
                await Assert.That(me.GetProperty("name").GetString()).IsEqualTo(user.Name);
                await Assert.That(me.GetProperty("onboardingCompleted").GetBoolean()).IsTrue();
                await Assert.That(!string.IsNullOrEmpty(me.GetProperty("organizationId").GetString()) && !string.IsNullOrEmpty(me.GetProperty("projectId").GetString()) &&
                    me.GetProperty("role").GetString() == "owner").IsTrue();
                await GotoAsync(page, "/onboarding");
                await Assert.That(BrowserAudit.WithoutWorkspace(page.Url)).IsEqualTo(origin + "/").Because("completed onboarding redirects to dashboard");
                await ShotAsync(page, "complete-" + size);
                await GotoAsync(page, "/organization?tab=billing");
                var summary = await page.Locator("#billing-summary").InnerTextAsync();
                await Assert.That(summary).Contains(paid ? "Starter" : "Free").Because("confirmed plan is persisted");
                if (paid)
                {
                    await Assert.That(summary).Contains("On file").Because("confirmed Stripe subscription stores payment status");
                }

                if (paid)
                {
                    await Assert.That(summary).Contains("Monthly").Because("confirmed subscription retains the default monthly interval");
                }

                checks.Add(size + " completed account/project persistence and billing");
            }
            // Logout ends the session at ts-auth; its hosted sign-in page (stood in here) is where the browser lands.
            async Task LogoutAsync(IPage page, bool keyboard = false)
            {
                await GotoAsync(page, "/");
                if (!await page.Locator("details.ts-account summary").IsVisibleAsync())
                {
                    await page.GetByRole(AriaRole.Button, new() { Name = "Open navigation", Exact = true }).ClickAsync();
                }

                await page.Locator("details.ts-account summary").ClickAsync();
                var logout = await SubmitAsync(page, "form:is([action=\"/api/v1/auth/logout\"],[action^=\"/api/v1/auth/logout?\"])", "Sign out", keyboard);
                await Assert.That(logout.Status).IsEqualTo(303);
                await Assert.That(logout.Headers.GetValueOrDefault("location")).IsEqualTo("/login").Because("the test host's end-session URL");
                await Assert.That(new Uri(page.Url).AbsolutePath).IsEqualTo("/__test/oidc");
                await Assert.That((await page.APIRequest.GetAsync(origin + "/api/v1/auth/me")).Status).IsEqualTo(401);
            }
            async Task FailedCallbackAsync()
            {
                var (context, page) = await NewContextAsync();
                await GotoAsync(page, "/login/callback?state=forged&code=test-code", 400);
                await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "This sign-in was already used, or was started in another browser." }).WaitForAsync();
                await BrowserAudit.AssertAccessibleAsync(page, "sign-in failure accessibility");
                await ShotAsync(page, "signin-failure");
                await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = "Try again", Exact = true }).GetAttributeAsync("href")).IsEqualTo("/login");
                var callbackRequest = page.WaitForRequestAsync(request => new Uri(request.Url).AbsolutePath == "/login/callback");
                await Assert.That((await BrowserAudit.SignInAsync(page, fixture.Origin, "/api/v1/auth/me"))!.Status).IsEqualTo(200);
                var callbackUrl = new Uri((await callbackRequest).Url);
                await Assert.That(Regex.IsMatch(QueryHelpers.ParseQuery(callbackUrl.Query).GetValueOrDefault("state").ToString(), "^[a-f0-9]{64}$")).IsTrue()
                    .Because("test-only OIDC producer returns the issued state");
                await Assert.That(page.Url).IsEqualTo(origin + "/api/v1/auth/me");
                await Assert.That((await context.APIRequest.GetAsync(origin + "/api/v1/auth/me")).Status).IsEqualTo(200);
                await GotoAsync(page, callbackUrl.PathAndQuery);
                await Assert.That(new Uri(page.Url).AbsolutePath).IsNotEqualTo("/login/callback");
                await LogoutAsync(page);
                var (_, other) = await NewContextAsync();
                await GotoAsync(other, callbackUrl.PathAndQuery, 400);
                await other.GetByRole(AriaRole.Status).Filter(new() { HasText = "This sign-in was already used, or was started in another browser." }).WaitForAsync();
                checks.Add("failed-callback recovery page, test-only producer callback, state verification, single-use callback and signed-in callback replay");
            }
        }

        private static async Task Denied(HttpClient client, HttpMethod method, string path, string cookie)
        {
            using var response = await Request(client, method, path, cookie);
            var body = await response.Content.ReadAsStringAsync();
            var mediaType = path.StartsWith("/api/", StringComparison.Ordinal) ? "application/problem+json" : "text/html";
            Check(response.StatusCode == HttpStatusCode.Forbidden && body.Contains("Your account does not have platform admin access.", StringComparison.Ordinal) &&
                body.Contains(response.Headers.GetValues("X-Request-ID").Single(), StringComparison.Ordinal) &&
                response.Content.Headers.ContentType?.MediaType == mediaType, "Ordinary owner cannot access administration: " + path);
        }

        private static Task<HttpResponseMessage> Request(HttpClient client, HttpMethod method, string path, string cookie)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-Platform-Admin", "true");
            request.Headers.Add("X-Role", "platform-admin");
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("invalid form: platform_admin=true");
            }

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
            {
                body = string.Join('\n', body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart()));
            }

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

        private static string SessionCookie(HttpResponseMessage response)
        {
            return response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class BrowserUser(string name, string email, string password)
        {
            public string Name { get; set; } = name;
            public string Email { get; } = email;
            public string Password { get; set; } = password;
        }
    }
}
