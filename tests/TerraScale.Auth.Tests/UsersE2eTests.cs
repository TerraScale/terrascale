namespace TerraScale.Auth.Tests;

using System.Net;
using Microsoft.Playwright;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Hosted user-facing UI flows over the real service, engine, and SDK:
/// user-visible page flows drive a real headless Chromium, while Location-header
/// redirect checks, token-consume races, and store-level semantics stay on the wire.</summary>
[NotInParallel]
public sealed class UsersE2eTests
{
    private static async Task<AuthServiceFixture> ServiceAsync() => await AuthServiceFixture.GetAsync();

    private static async Task<(string InstanceId, string Slug)> InstanceAsync()
    {
        var service = await ServiceAsync();
        var (id, slug, _) = await service.CreateInstanceAsync("users-" + Guid.NewGuid().ToString("N")[..8]);
        return (id, slug);
    }

    private static string Email() => $"u{Guid.NewGuid().ToString("N")[..10]}@example.com";

    private static string Url(AuthServiceFixture service, string path) => new Uri(service.Origin, path).ToString();

    private static HttpRequestMessage Post(string path, Dictionary<string, string> fields, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Get(string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    /// <summary>Extracts the "tsauth_session=token" pair from a Set-Cookie header.</summary>
    private static async Task<string> SessionCookieAsync(HttpResponseMessage response)
    {
        var header = (response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null)
            ?? throw new InvalidOperationException("expected a session cookie");
        return header.Split(';')[0];
    }

    /// <summary>Signs in over HTTP and returns (cookie header, session id from the store).</summary>
    private static async Task<(string Cookie, string SessionId)> SignInAsync(AuthServiceFixture service, string slug, string email, string password = "password123")
    {
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var cookie = await SessionCookieAsync(response);
        var session = await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(cookie["tsauth_session=".Length..]))
            ?? throw new InvalidOperationException("session missing from store");
        return (cookie, session.Id);
    }

    /// <summary>Signs up over HTTP and returns (cookie header, session id from the store).</summary>
    private static async Task<(string Cookie, string SessionId)> SignUpAsync(AuthServiceFixture service, string slug, string email, string password = "password123")
    {
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["name"] = "Test User",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var cookie = await SessionCookieAsync(response);
        var session = await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(cookie["tsauth_session=".Length..]))
            ?? throw new InvalidOperationException("session missing from store");
        return (cookie, session.Id);
    }

    /// <summary>Creates a verification token with a known plaintext token value so tests can hit consume endpoints.</summary>
    private static async Task<string> KnownTokenAsync(AuthStore store, string instanceId, string? userId, string purpose, TimeSpan ttl)
    {
        var token = "e2e" + SecureTokens.RandomToken(32);
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            Purpose = purpose,
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        return token;
    }

    /// <summary>Fails the test if any script dialog (alert/confirm/prompt) ever fires on the page.</summary>
    private static void FailOnDialog(IPage page)
    {
        page.Dialog += (_, dialog) => throw new InvalidOperationException(
            $"unexpected script dialog ({dialog.Type}): {dialog.Message}");
    }

    /// <summary>Signs up through the real sign-up page; the browser follows the redirect to /account.</summary>
    private static async Task<IPage> SignUpInBrowserAsync(AuthServiceFixture service, string slug, string email)
    {
        var page = await BrowserFixture.NewPageAsync();
        await page.GotoAsync(Url(service, $"/ui/{slug}/sign-up"));
        await page.FillAsync("#su-email", email);
        await page.FillAsync("#su-password", "password123");
        await page.FillAsync("#su-name", "Test User");
        await page.ClickAsync("button:has-text('Create account')");
        await page.WaitForURLAsync("**/account");
        return page;
    }

    /// <summary>Resolves the browser's session in the store from its (HttpOnly) cookie, exactly like the service does.</summary>
    private static async Task<Session> BrowserSessionAsync(AuthServiceFixture service, IPage page)
    {
        var token = (await page.Context.CookiesAsync(service.Origin.ToString()))
            .Single(c => c.Name == "tsauth_session").Value;
        return await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(token))
            ?? throw new InvalidOperationException("session missing from store");
    }

    /// <summary>Waits for the next POST to <paramref name="url"/> (with or without a query string —
    /// action-less forms post to the current URL) and returns its raw response.</summary>
    private static Task<IResponse> WaitForPostAsync(IPage page, string url) =>
        page.WaitForResponseAsync(r => r.Request.Method == "POST"
            && (r.Url == url || r.Url.StartsWith(url + "?", StringComparison.Ordinal)));

    [Test]
    public async Task UnknownInstanceSlugShows404()
    {
        var service = await ServiceAsync();
        await using var page = await BrowserFixture.NewPageAsync();
        var response = await page.GotoAsync(Url(service, $"/ui/does-not-exist-{Guid.NewGuid().ToString("N")[..6]}/sign-in"));
        await Assert.That(response!.Status).IsEqualTo((int)HttpStatusCode.NotFound);
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains("Page not found");
    }

    [Test]
    public async Task SignUpSignInPageRenders()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        await using var page = await BrowserFixture.NewPageAsync();

        var signUp = await page.GotoAsync(Url(service, $"/ui/{slug}/sign-up"));
        await Assert.That(signUp!.Status).IsEqualTo((int)HttpStatusCode.OK);
        await Assert.That(await page.Locator("input[name=password]").CountAsync()).IsEqualTo(1);
        await Assert.That(await page.Locator("input[name=name]").CountAsync()).IsEqualTo(1);

        var signIn = await page.GotoAsync(Url(service, $"/ui/{slug}/sign-in"));
        await Assert.That(signIn!.Status).IsEqualTo((int)HttpStatusCode.OK);
        await Assert.That(await page.Locator($"form[action='/ui/{slug}/magic-link']").CountAsync()).IsEqualTo(1);
        await Assert.That(await page.Locator($"a[href='/ui/{slug}/passkeys/assert']").CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task SignUpSignsInAndIssuesVerificationToken()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();

        await using var page = await SignUpInBrowserAsync(service, slug, email);

        var text = await page.Locator("body").TextContentAsync();
        await Assert.That(text).Contains(email);
        await Assert.That(text).Contains("email not verified");

        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(user!.EmailVerified).IsFalse();
        await Assert.That(user!.LastSignInAt).IsNotNull();

        var tokens = await service.Store.ListVerificationTokensAsync(instanceId, "verify_email", 100);
        await Assert.That(tokens.Any(t => t.InstanceId == instanceId && t.UserId == user.Id && !t.Consumed)).IsTrue();

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.signed_in" && e.Data.TryGetValue("method", out var method) && method.ToString() == "password")).IsTrue();
        await Assert.That(audits.Any(e => e.Type == "user.signed_up")).IsTrue();
    }

    [Test]
    public async Task DuplicateSignUpShowsFriendlyError()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await using var setup = await SignUpInBrowserAsync(service, slug, email);

        await using var page = await BrowserFixture.NewPageAsync();
        FailOnDialog(page);
        await page.GotoAsync(Url(service, $"/ui/{slug}/sign-up"));
        await page.FillAsync("#su-email", email);
        await page.FillAsync("#su-password", "password123");
        var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/sign-up"));
        await page.ClickAsync("button:has-text('Create account')");
        await Assert.That((await posting).Status).IsEqualTo((int)HttpStatusCode.OK);
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("already registered");
        // The email round-trips as the input's VALUE, never as markup.
        await Assert.That(await page.InputValueAsync("#su-email")).IsEqualTo(email);
    }

    [Test]
    public async Task InvalidSignUpInputReRendersWithError()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        await using var page = await BrowserFixture.NewPageAsync();
        FailOnDialog(page);
        var url = Url(service, $"/ui/{slug}/sign-up");

        foreach (var (email, password, expected) in new (string Email, string Password, string Expected)[]
        {
            ("not-an-email", "password123", "valid email"),
            (Email(), "short", "at least 8 characters"),
            ("a@b.example.com@c", "password123", "valid email"),
        })
        {
            await page.GotoAsync(url);
            await page.FillAsync("#su-email", email);
            await page.FillAsync("#su-password", password);
            // Submit through the DOM API: type=email and minlength=8 client validation would
            // block these values, and the server-side re-render is the thing under test.
            var posting = WaitForPostAsync(page, url);
            await page.EvalOnSelectorAsync("form", "form => form.submit()");
            await Assert.That((await posting).Status).IsEqualTo((int)HttpStatusCode.OK);
            await page.Locator(".error").WaitForAsync();
            await Assert.That(await page.Locator(".error").TextContentAsync()).Contains(expected);
            // The submitted email round-trips as the input's VALUE, never as markup.
            await Assert.That(await page.InputValueAsync("#su-email")).IsEqualTo(email);
        }
    }

    [Test]
    public async Task WrongPasswordOrUnknownEmailIsGenericAndSetsNoCookie()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await using var setup = await SignUpInBrowserAsync(service, slug, email);

        await using var page = await BrowserFixture.NewPageAsync();
        var url = Url(service, $"/ui/{slug}/sign-in");
        foreach (var (password, address) in new[] { ("wrongpassword", email), ("password123", "nobody@example.com") })
        {
            await page.GotoAsync(url);
            await page.FillAsync("#si-email", address);
            await page.FillAsync("#si-password", password);
            var posting = WaitForPostAsync(page, url);
            await page.ClickAsync("button:has-text('Sign in')");
            var response = await posting;
            await Assert.That(response.Status).IsEqualTo((int)HttpStatusCode.OK);
            await Assert.That(response.Headers.ContainsKey("set-cookie")).IsFalse();
            await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("Invalid email or password");
        }
    }

    [Test]
    public async Task PasswordlessUsersCannotSignInWithAnyPassword()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var noPassword = Email();
        var emptyPassword = Email();

        // SSO/management style creation. An explicit empty string must mean
        // password-less too — a hash of "" would let anyone sign in with no credential.
        var sessions = new SessionService(service.Store);
        await sessions.CreateUserAsync(instanceId, noPassword, username: null, password: null, name: null);
        var createdEmpty = await sessions.CreateUserAsync(instanceId, emptyPassword, username: null, password: "", name: null);
        await Assert.That(createdEmpty.PasswordHash).IsNull();
        await Assert.That((await service.Store.FindUserByEmailAsync(instanceId, noPassword))!.PasswordHash).IsNull();

        await using var page = await BrowserFixture.NewPageAsync();
        var url = Url(service, $"/ui/{slug}/sign-in");
        foreach (var (email, password) in new[] { (noPassword, "password123"), (emptyPassword, ""), (emptyPassword, "password123") })
        {
            await page.GotoAsync(url);
            await page.FillAsync("#si-email", email);
            await page.FillAsync("#si-password", password);
            // Submit through the DOM API: the empty password would trip the browser's required check.
            var posting = WaitForPostAsync(page, url);
            await page.EvalOnSelectorAsync("form", "form => form.submit()");
            var response = await posting;
            await Assert.That(response.Status).IsEqualTo((int)HttpStatusCode.OK);
            await Assert.That(response.Headers.ContainsKey("set-cookie")).IsFalse();
            await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("Invalid email or password");
        }
    }

    [Test]
    public async Task SignInPasswordCheckBurnsArgon2idWorkOnEveryFailurePath()
    {
        // Response timing must not reveal which emails are registered: every
        // failure path burns one full Argon2id verification, like a real mismatch.
        var known = new User
        {
            Id = "usr_x",
            InstanceId = "inst",
            PasswordHash = PasswordHasher.Hash("password123"),
            CreatedAt = 0,
            UpdatedAt = 0,
        };
        var cases = new (User? User, string Password)[]
        {
            (null, "password123"),                            // unknown email
            (known with { Deleted = true }, "password123"),   // deleted user
            (known with { PasswordHash = null }, "whatever"), // password-less user
            (known, "wrongpassword"),                         // wrong password
        };
        foreach (var (user, password) in cases)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var ok = SessionService.VerifySignInPassword(user, password);
            watch.Stop();
            await Assert.That(ok).IsFalse();
            await Assert.That(watch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(10);
        }

        await Assert.That(SessionService.VerifySignInPassword(known, "password123")).IsTrue();
    }

    [Test]
    public async Task BannedAccountShowsSuspended()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        await service.Store.UpsertUserAsync(user! with { Banned = true });

        await using var page = await BrowserFixture.NewPageAsync();
        var url = Url(service, $"/ui/{slug}/sign-in");
        await page.GotoAsync(url);
        await page.FillAsync("#si-email", email);
        await page.FillAsync("#si-password", "password123");
        var posting = WaitForPostAsync(page, url);
        await page.ClickAsync("button:has-text('Sign in')");
        var response = await posting;
        await Assert.That(response.Status).IsEqualTo((int)HttpStatusCode.OK);
        await Assert.That(response.Headers.ContainsKey("set-cookie")).IsFalse();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("suspended");
    }

    [Test]
    public async Task SignInOnlyRedirectsToSafeNextTargets()
    {
        // Location-header matrix: stays on the wire (service.Http has no auto-redirect).
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        foreach (var next in new[]
        {
            "https://evil.example/home",
            "//evil.example",
            "https://auth.test.evil.example",
            // Browsers treat "\" as "/" in URLs, so "/\evil.example" reaches "//evil.example".
            "/\\evil.example",
            "/\\/evil.example",
            "\\/evil.example",
            // Browsers strip control characters before parsing, so "/<TAB>/…" also becomes "//…".
            "/\t/evil.example",
        })
        {
            using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = "password123",
                ["next"] = next,
            }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
            await Assert.That(response.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/account");
        }

        using var sameOrigin = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
            ["next"] = $"{service.Origin}ui/{slug}/account",
        }));
        await Assert.That(sameOrigin.Headers.Location?.ToString()).IsEqualTo($"{service.Origin}ui/{slug}/account");

        using var safe = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
            ["next"] = $"/ui/{slug}/account/sessions",
        }));
        await Assert.That(safe.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/account/sessions");
    }

    [Test]
    public async Task SignOutRevokesSessionAndClearsCookie()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await using var page = await SignUpInBrowserAsync(service, slug, email);
        var session = await BrowserSessionAsync(service, page);

        var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/sign-out"));
        await page.ClickAsync("button:text-is('Sign out')");
        var signOut = await posting;
        await Assert.That(signOut.Status).IsEqualTo((int)HttpStatusCode.Found);
        await Assert.That(signOut.Headers["location"]).IsEqualTo($"/ui/{slug}/sign-in");
        await page.WaitForURLAsync("**/sign-in");

        // The cleared cookie is gone from the browser's jar.
        await Assert.That((await page.Context.CookiesAsync(service.Origin.ToString()))
            .Count(c => c.Name == "tsauth_session")).IsEqualTo(0);

        await Assert.That((await service.Store.GetSessionAsync(session.Id))!.Status).IsEqualTo("revoked");

        await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(page.Url).Contains("sign-in");
    }

    [Test]
    public async Task AccountRequiresSession()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        await using var page = await BrowserFixture.NewPageAsync();

        // Anonymous: bounced to sign-in with the original path carried in ?next=.
        await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(page.Url).IsEqualTo(Url(service, $"/ui/{slug}/sign-in?next={Uri.EscapeDataString($"/ui/{slug}/account")}"));

        // A garbage session cookie is just as anonymous.
        await page.Context.AddCookiesAsync([new() { Name = "tsauth_session", Value = "garbage", Url = service.Origin.ToString() }]);
        await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(page.Url).Contains("sign-in");
    }

    [Test]
    public async Task ChangePasswordRejectsWrongCurrentAndRotates()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await using var page = await SignUpInBrowserAsync(service, slug, email);
        var changeUrl = Url(service, $"/ui/{slug}/account/password");

        await page.FillAsync("#cp-current", "not-the-password");
        await page.FillAsync("#cp-new", "newpassword9");
        var posting = WaitForPostAsync(page, changeUrl);
        await page.ClickAsync("button:has-text('Change password')");
        var wrong = await posting;
        await Assert.That(wrong.Status).IsEqualTo((int)HttpStatusCode.OK);
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("current password is incorrect");

        await page.FillAsync("#cp-current", "password123");
        await page.FillAsync("#cp-new", "newpassword9");
        posting = WaitForPostAsync(page, changeUrl);
        await page.ClickAsync("button:has-text('Change password')");
        var changed = await posting;
        await Assert.That(changed.Status).IsEqualTo((int)HttpStatusCode.Found);
        await Assert.That(changed.Headers["location"]).IsEqualTo($"/ui/{slug}/account");

        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(SessionService.VerifyPassword(user!, "newpassword9")).IsTrue();
        await Assert.That(SessionService.VerifyPassword(user!, "password123")).IsFalse();

        await page.GotoAsync(Url(service, $"/ui/{slug}/sign-in"));
        await page.FillAsync("#si-email", email);
        await page.FillAsync("#si-password", "password123");
        await page.ClickAsync("button:has-text('Sign in')");
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("Invalid email or password");

        await page.FillAsync("#si-password", "newpassword9");
        await page.ClickAsync("button:has-text('Sign in')");
        await page.WaitForURLAsync("**/account");

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.password_changed")).IsTrue();
    }

    [Test]
    public async Task ChangePasswordRevokesOtherSessionsButKeepsCurrent()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await using var page = await SignUpInBrowserAsync(service, slug, email);
        var current = await BrowserSessionAsync(service, page);
        var (_, otherSessionId) = await SignInAsync(service, slug, email);

        await page.FillAsync("#cp-current", "password123");
        await page.FillAsync("#cp-new", "newpassword9");
        var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/account/password"));
        await page.ClickAsync("button:has-text('Change password')");
        await Assert.That((await posting).Status).IsEqualTo((int)HttpStatusCode.Found);

        await Assert.That((await service.Store.GetSessionAsync(otherSessionId))!.Status).IsEqualTo("revoked");
        await Assert.That((await service.Store.GetSessionAsync(current.Id))!.Status).IsEqualTo("active");

        var stillSignedIn = await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(stillSignedIn!.Status).IsEqualTo((int)HttpStatusCode.OK);
    }

    [Test]
    public async Task RevokeAllSessionsSignsOutEverywhere()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await using var page = await SignUpInBrowserAsync(service, slug, email);
        var session1 = (await BrowserSessionAsync(service, page)).Id;
        var (_, session2) = await SignInAsync(service, slug, email);
        var (_, session3) = await SignInAsync(service, slug, email);

        var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/account/sessions/revoke-all"));
        await page.ClickAsync("button:has-text('Sign out everywhere')");
        var response = await posting;
        await Assert.That(response.Status).IsEqualTo((int)HttpStatusCode.Found);
        await Assert.That(response.Headers["location"]).IsEqualTo($"/ui/{slug}/sign-in");
        await page.WaitForURLAsync("**/sign-in");

        await Assert.That((await service.Store.GetSessionAsync(session1))!.Status).IsEqualTo("revoked");
        await Assert.That((await service.Store.GetSessionAsync(session2))!.Status).IsEqualTo("revoked");
        await Assert.That((await service.Store.GetSessionAsync(session3))!.Status).IsEqualTo("revoked");
    }

    [Test]
    public async Task SessionsPageListsDevicesAndRevokesOne()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await using var page = await SignUpInBrowserAsync(service, slug, email);
        var (_, otherSessionId) = await SignInAsync(service, slug, email);

        await page.GotoAsync(Url(service, $"/ui/{slug}/account/sessions"));
        var text = await page.Locator("body").TextContentAsync();
        await Assert.That(text).Contains("this device");
        await Assert.That(text).Contains("Revoke");

        // The other device's row: the one NOT marked "this device".
        var otherRow = page.Locator(".row").Filter(new LocatorFilterOptions { HasNotText = "this device" });
        await Assert.That(await otherRow.CountAsync()).IsEqualTo(1);
        var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/account/sessions/{otherSessionId}/revoke"));
        await otherRow.Locator("button").ClickAsync();
        var revoke = await posting;
        await Assert.That(revoke.Status).IsEqualTo((int)HttpStatusCode.Found);
        await Assert.That(revoke.Headers["location"]).IsEqualTo($"/ui/{slug}/account/sessions");

        var other = await service.Store.GetSessionAsync(otherSessionId);
        await Assert.That(other!.Status).IsEqualTo("revoked");

        var stillWorks = await page.GotoAsync(Url(service, $"/ui/{slug}/account/sessions"));
        await Assert.That(stillWorks!.Status).IsEqualTo((int)HttpStatusCode.OK);
    }

    [Test]
    public async Task SessionCookieUsesStrictFlags()
    {
        // The raw Set-Cookie of the real sign-up POST, captured from the browser's network stack.
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        await using var page = await BrowserFixture.NewPageAsync();

        var url = Url(service, $"/ui/{slug}/sign-up");
        await page.GotoAsync(url);
        await page.FillAsync("#su-email", Email());
        await page.FillAsync("#su-password", "password123");
        await page.FillAsync("#su-name", "Test User");
        var posting = WaitForPostAsync(page, url);
        await page.ClickAsync("button:has-text('Create account')");
        var response = await posting;
        await Assert.That(response.Status).IsEqualTo((int)HttpStatusCode.Found);
        // AllHeaders merges the network stack's "extra info" headers — Set-Cookie lives there.
        var headers = await response.AllHeadersAsync();
        var header = (headers.TryGetValue("set-cookie", out var setCookie) ? setCookie : "").ToLowerInvariant();
        await Assert.That(header).Contains("httponly");
        await Assert.That(header).Contains("secure");
        await Assert.That(header).Contains("samesite=lax");
        await Assert.That(header).Contains("path=/");
        await Assert.That(header.Contains("domain=")).IsFalse();
    }

    [Test]
    public async Task ExpiredSessionIsRejected()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await using var page = await SignUpInBrowserAsync(service, slug, email);
        var session = await BrowserSessionAsync(service, page);

        await service.Store.UpsertSessionAsync(session with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds() });

        await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(page.Url).Contains("sign-in");
    }

    [Test]
    public async Task BannedOrDeletedUserSessionsAreRejected()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await using var page = await SignUpInBrowserAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);

        await service.Store.UpsertUserAsync(user! with { Banned = true });
        await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(page.Url).Contains("sign-in");

        await service.Store.UpsertUserAsync(user with { Banned = false, Deleted = true });
        await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(page.Url).Contains("sign-in");
    }

    [Test]
    public async Task SessionCookieDoesNotWorkAcrossInstances()
    {
        var service = await ServiceAsync();
        var (_, slugA) = await InstanceAsync();
        var (_, slugB) = await InstanceAsync();
        await using var page = await SignUpInBrowserAsync(service, slugA, Email());

        // The A-session bounces off instance B straight to B's sign-in, B's account path in ?next=.
        await page.GotoAsync(Url(service, $"/ui/{slugB}/account"));
        await Assert.That(page.Url).IsEqualTo(Url(service, $"/ui/{slugB}/sign-in?next={Uri.EscapeDataString($"/ui/{slugB}/account")}"));
    }

    [Test]
    public async Task ResolveThrottlesLastActiveAtWrites()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, sessionId) = await SignUpAsync(service, slug, email);
        var sessions = new SessionService(service.Store);
        var token = cookie["tsauth_session=".Length..];

        await sessions.ResolveAsync(instanceId, token);
        var first = (await service.Store.GetSessionAsync(sessionId))!.LastActiveAt;
        await Task.Delay(1100);
        await sessions.ResolveAsync(instanceId, token);
        var second = (await service.Store.GetSessionAsync(sessionId))!.LastActiveAt;
        await Assert.That(second).IsEqualTo(first);

        var session = await service.Store.GetSessionAsync(sessionId);
        await service.Store.UpsertSessionAsync(session! with { LastActiveAt = DateTimeOffset.UtcNow.AddSeconds(-301).ToUnixTimeSeconds() });
        await sessions.ResolveAsync(instanceId, token);
        var refreshed = (await service.Store.GetSessionAsync(sessionId))!.LastActiveAt;
        await Assert.That(refreshed > first).IsTrue();
    }

    [Test]
    public async Task RevokedSessionCannotBeTouchedBackToActive()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var (_, sessionId) = await SignUpAsync(service, slug, Email());
        var session = await service.Store.GetSessionAsync(sessionId);
        await service.Store.UpsertSessionAsync(session! with { Status = "revoked" });

        var touched = await service.Store.TryTouchSessionAsync(
            sessionId,
            instanceId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await Assert.That(touched).IsFalse();
        await Assert.That((await service.Store.GetSessionAsync(sessionId))!.Status).IsEqualTo("revoked");
    }

    [Test]
    public async Task MagicLinkAlwaysShowsCheckEmailPage()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        await using var page = await BrowserFixture.NewPageAsync();
        foreach (var address in new[] { email, "nobody@example.com" })
        {
            await page.GotoAsync(Url(service, $"/ui/{slug}/sign-in"));
            await page.FillAsync("#si-magic", address);
            var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/magic-link"));
            await page.ClickAsync("button:has-text('Send magic link')");
            await Assert.That((await posting).Status).IsEqualTo((int)HttpStatusCode.OK);
            await page.GetByText("Check your email").WaitForAsync();
            await Assert.That(await page.Locator("body").TextContentAsync()).Contains("Check your email");
        }
    }

    [Test]
    public async Task MagicLinkConsumeSignsInAndIsSingleUse()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "magic_link", TimeSpan.FromMinutes(10));

        await using var page = await BrowserFixture.NewPageAsync();
        var consume = Url(service, $"/ui/{slug}/magic-link/consume?token={token}");
        await page.GotoAsync(consume);
        await page.WaitForURLAsync("**/account");
        await Assert.That(page.Url).IsEqualTo(Url(service, $"/ui/{slug}/account"));
        await Assert.That((await page.Context.CookiesAsync(service.Origin.ToString())).Any(c => c.Name == "tsauth_session")).IsTrue();
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains(email);

        var replay = await page.GotoAsync(consume);
        await Assert.That(replay!.Status).IsEqualTo((int)HttpStatusCode.BadRequest);
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains("no longer valid");

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.signed_in" && e.Data.TryGetValue("method", out var method) && method.ToString() == "magic_link")).IsTrue();
    }

    [Test]
    public async Task MagicLinkConsumeIsAtomicUnderConcurrentDoubleRequest()
    {
        // Consume race: stays on the wire (browser-driving a race makes it flaky, not better).
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "magic_link", TimeSpan.FromMinutes(10));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token={token}"))));
        foreach (var response in responses)
            response.Dispose();

        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Found)).IsEqualTo(1);
        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest)).IsEqualTo(5);
    }

    [Test]
    public async Task BadTokensAreRejected()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        await using var page = await BrowserFixture.NewPageAsync();
        var consume = Url(service, $"/ui/{slug}/magic-link/consume?token=");

        var garbage = await page.GotoAsync(consume + "garbage");
        await Assert.That(garbage!.Status).IsEqualTo((int)HttpStatusCode.BadRequest);

        var expired = await KnownTokenAsync(service.Store, instanceId, user!.Id, "magic_link", TimeSpan.FromMinutes(-5));
        var expiredResponse = await page.GotoAsync(consume + expired);
        await Assert.That(expiredResponse!.Status).IsEqualTo((int)HttpStatusCode.BadRequest);
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains("no longer valid");

        var wrongPurpose = await KnownTokenAsync(service.Store, instanceId, user.Id, "verify_email", TimeSpan.FromMinutes(10));
        var wrongPurposeResponse = await page.GotoAsync(consume + wrongPurpose);
        await Assert.That(wrongPurposeResponse!.Status).IsEqualTo((int)HttpStatusCode.BadRequest);

        var (otherInstanceId, _) = await InstanceAsync();
        var foreignToken = await KnownTokenAsync(service.Store, otherInstanceId, user.Id, "magic_link", TimeSpan.FromMinutes(10));
        var foreign = await page.GotoAsync(consume + foreignToken);
        await Assert.That(foreign!.Status).IsEqualTo((int)HttpStatusCode.BadRequest);
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains("no longer valid");
    }

    [Test]
    public async Task ForgotPasswordShowsCheckEmailAndResetForm()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);

        await using var page = await BrowserFixture.NewPageAsync();
        foreach (var address in new[] { email, "nobody@example.com" })
        {
            await page.GotoAsync(Url(service, $"/ui/{slug}/forgot-password"));
            await page.FillAsync("#fp-email", address);
            var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/forgot-password"));
            await page.ClickAsync("button:has-text('Send reset link')");
            await Assert.That((await posting).Status).IsEqualTo((int)HttpStatusCode.OK);
            await page.GetByText("Check your email").WaitForAsync();
            await Assert.That(await page.Locator("body").TextContentAsync()).Contains("Check your email");
        }

        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "reset_password", TimeSpan.FromMinutes(30));
        var form = await page.GotoAsync(Url(service, $"/ui/{slug}/reset-password?token={token}"));
        await Assert.That(form!.Status).IsEqualTo((int)HttpStatusCode.OK);
        await Assert.That(await page.Locator("input[name=password]").CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ResetPasswordUpdatesHashAndRevokesAllSessions()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        var (_, sessionId) = await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "reset_password", TimeSpan.FromMinutes(30));

        await using var page = await BrowserFixture.NewPageAsync();
        var reset = Url(service, $"/ui/{slug}/reset-password?token={token}");
        await page.GotoAsync(reset);
        await page.FillAsync("#rp-password", "short");
        // Submit through the DOM API so minlength=8 does not hide the server-side re-render.
        await page.EvalOnSelectorAsync("form", "form => form.submit()");
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("at least 8 characters");

        await page.FillAsync("#rp-password", "freshpassword1");
        await page.ClickAsync("button:has-text('Update password')");
        await page.GetByText("Password updated").WaitForAsync();
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains("updated");

        var after = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(SessionService.VerifyPassword(after!, "freshpassword1")).IsTrue();
        await Assert.That((await service.Store.GetSessionAsync(sessionId))!.Status).IsEqualTo("revoked");

        var replay = await page.GotoAsync(reset);
        await Assert.That(replay!.Status).IsEqualTo((int)HttpStatusCode.BadRequest);

        using var replayPost = await service.Http.SendAsync(Post($"/ui/{slug}/reset-password", new Dictionary<string, string>
        {
            ["token"] = token,
            ["password"] = "anotherpassword1",
        }));
        await Assert.That(replayPost.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        await page.GotoAsync(Url(service, $"/ui/{slug}/sign-in"));
        await page.FillAsync("#si-email", email);
        await page.FillAsync("#si-password", "freshpassword1");
        await page.ClickAsync("button:has-text('Sign in')");
        await page.WaitForURLAsync("**/account");

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "password.reset")).IsTrue();
    }

    [Test]
    public async Task BannedUserCannotRequestOrUsePasswordReset()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "reset_password", TimeSpan.FromMinutes(30));

        await service.Store.UpsertUserAsync(user with { Banned = true });

        await using var page = await BrowserFixture.NewPageAsync();
        await page.GotoAsync(Url(service, $"/ui/{slug}/forgot-password"));
        await page.FillAsync("#fp-email", email);
        var posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/forgot-password"));
        await page.ClickAsync("button:has-text('Send reset link')");
        await Assert.That((await posting).Status).IsEqualTo((int)HttpStatusCode.OK);
        await page.GetByText("Check your email").WaitForAsync();
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains("Check your email");

        var unconsumed = (await service.Store.ListVerificationTokensAsync(instanceId, "reset_password", 100))
            .Where(t => t.InstanceId == instanceId && t.UserId == user.Id && !t.Consumed).ToList();
        await Assert.That(unconsumed.Count).IsEqualTo(1); // only the pre-ban KnownTokenAsync one

        // The banned user's still-valid token: the form renders, but submitting fails.
        await page.GotoAsync(Url(service, $"/ui/{slug}/reset-password?token={token}"));
        await page.FillAsync("#rp-password", "attackerpassword1");
        posting = WaitForPostAsync(page, Url(service, $"/ui/{slug}/reset-password"));
        await page.ClickAsync("button:has-text('Update password')");
        await Assert.That((await posting).Status).IsEqualTo((int)HttpStatusCode.BadRequest);

        var after = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(SessionService.VerifyPassword(after!, "password123")).IsTrue();
    }

    [Test]
    public async Task VerifyEmailMarksUserVerified()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "verify_email", TimeSpan.FromHours(24));

        await using var page = await BrowserFixture.NewPageAsync();
        var verify = await page.GotoAsync(Url(service, $"/ui/{slug}/verify-email?token={token}"));
        await Assert.That(verify!.Status).IsEqualTo((int)HttpStatusCode.OK);
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains("verified");

        var after = await service.Store.GetUserAsync(user.Id);
        await Assert.That(after!.EmailVerified).IsTrue();

        var replay = await page.GotoAsync(Url(service, $"/ui/{slug}/verify-email?token={token}"));
        await Assert.That(replay!.Status).IsEqualTo((int)HttpStatusCode.BadRequest);

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.email_verified")).IsTrue();
    }
}
