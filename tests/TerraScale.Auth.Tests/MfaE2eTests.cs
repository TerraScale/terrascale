namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>TOTP enrollment, the sign-in second step, recovery codes, and the MFA management API.</summary>
[NotInParallel]
public sealed class MfaE2eTests
{
    [Test]
    public async Task EnrollPageRequiresSession()
    {
        var c = await ArrangeAsync("page");
        await using var page = await BrowserFixture.NewPageAsync();

        // The browser follows the redirect: a signed-out visitor lands on sign-in.
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/enroll"));
        await Assert.That(page.Url).Contains("/sign-in");
    }

    [Test]
    public async Task EnrollmentShowsSecretAndIssuesRecoveryCodes()
    {
        var c = await ArrangeAsync("enroll");
        await using var page = await SessionPageAsync(c);

        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/enroll"));
        await page.ClickAsync("button:has-text('Set up authenticator')");

        var totp = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);
        // The setup page shows exactly the secret the store holds encrypted.
        await Assert.That(Encodings.Base32Encode(secret).Length).IsEqualTo(32);
        await Assert.That(await page.Locator(".row code").First.TextContentAsync()).IsEqualTo(Encodings.Base32Encode(secret));

        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Confirm')");
        await page.Locator(".list .row code").First.WaitForAsync();
        var codes = (await page.Locator(".list .row code").AllTextContentsAsync()).ToArray();
        await Assert.That(codes.Length).IsEqualTo(10);

        var factors = await c.Store.ListMfaFactorsAsync(c.User.Id);
        var confirmed = factors.Single(f => f.Type == "totp");
        await Assert.That(confirmed.Confirmed).IsTrue();

        var recovery = factors.Single(f => f.Type == "recovery");
        await Assert.That(recovery.RecoveryCodes!.Length).IsEqualTo(10);
        await Assert.That(recovery.RecoveryCodes!.Contains(SecureTokens.Sha256Hex(codes[0]))).IsTrue();
        await Assert.That(recovery.RecoveryCodes!.Contains(codes[0])).IsFalse();

        var events = await c.Store.ListAuditEventsAsync(c.InstanceId, 100);
        await Assert.That(events.Any(e => e.Type == "mfa.enrolled")).IsTrue();
    }

    [Test]
    public async Task WrongConfirmCodeKeepsFactorUnconfirmedThenCorrectCodeConfirms()
    {
        var c = await ArrangeAsync("wrong");
        await using var page = await SessionPageAsync(c);
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/enroll"));
        await page.ClickAsync("button:has-text('Set up authenticator')");
        var totp = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);

        // A code from 10 minutes ago is far outside the ±30s verify window.
        var stale = Totp.Compute(secret, DateTimeOffset.UtcNow.AddMinutes(-10));
        await page.FillAsync("#code", stale);
        await page.ClickAsync("button:has-text('Confirm')");
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("Try again");
        await Assert.That((await c.Store.GetMfaFactorAsync(totp.Id))!.Confirmed).IsFalse();

        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Confirm')");
        await page.Locator(".list .row code").First.WaitForAsync();
        await Assert.That((await c.Store.GetMfaFactorAsync(totp.Id))!.Confirmed).IsTrue();
    }

    [Test]
    public async Task UnconfirmedFactorDoesNotGateSignInOrVerifyCodes()
    {
        var c = await ArrangeAsync("unconfirmed");
        await using (var sessionPage = await SessionPageAsync(c))
        {
            await sessionPage.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/enroll"));
            await sessionPage.ClickAsync("button:has-text('Set up authenticator')");
        }
        var totp = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);

        // Password sign-in issues a session directly: an unconfirmed factor never routes to the MFA gate.
        await using var page = await BrowserFixture.NewPageAsync();
        await SignInAsync(page, c);
        await page.WaitForURLAsync("**/account");
        await Assert.That(page.Url).DoesNotContain("mfa/verify");
        await Assert.That(await HasSessionCookieAsync(page, c)).IsTrue();

        // The not-yet-confirmed secret never verifies at the MFA gate either.
        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/verify?token={token}"));
        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Verify')");
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("Invalid code");
    }

    [Test]
    public async Task ConfirmingAnAlreadyConfirmedFactorIsRejected()
    {
        var c = await ArrangeAsync("reconfirm");
        await using var page = await SessionPageAsync(c);
        var (secret, codes, factors) = await EnrollAsync(c, page);
        var totp = factors.Single(f => f.Type == "totp");

        // A stale re-submission of the confirm form (back button + resubmit) on the
        // already-confirmed factor: not confirmable anymore.
        var replay = page.WaitForResponseAsync(r => r.Url.Contains("/mfa/enroll/confirm"));
        await SubmitFormAsync(page, c, $"/ui/{c.Slug}/mfa/enroll/confirm?factor={totp.Id}",
            ("code", Totp.Compute(secret, DateTimeOffset.UtcNow)));
        await Assert.That((await replay).Status).IsEqualTo(404);

        // The already-saved recovery set was not silently re-minted.
        var recovery = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "recovery");
        await Assert.That(recovery.RecoveryCodes!.Contains(SecureTokens.Sha256Hex(codes[0]))).IsTrue();
    }

    [Test]
    public async Task SecondConfirmedTotpEnrollmentIsRejected()
    {
        var c = await ArrangeAsync("dupe");
        await using var page = await SessionPageAsync(c);
        await EnrollAsync(c, page);

        var start = page.WaitForResponseAsync(r => r.Url.EndsWith("/mfa/enroll/start"));
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/enroll"));
        await page.ClickAsync("button:has-text('Set up authenticator')");
        await Assert.That((await start).Status).IsEqualTo(409);
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("already have a confirmed authenticator");
        await Assert.That((await c.Store.ListMfaFactorsAsync(c.User.Id)).Count(f => f.Type == "totp")).IsEqualTo(1);
    }

    [Test]
    public async Task VerifyWithCorrectCodeCreatesSessionAndRedirects()
    {
        var c = await ArrangeAsync("verify");
        var (secret, _, _) = await EnrollAsync(c);

        // Step-up through the real sign-in page: the gate intercepts the password
        // sign-in and the browser follows the pending redirect to the verify form.
        await using var page = await BrowserFixture.NewPageAsync();
        await SignInAsync(page, c, next: $"/ui/{c.Slug}/account?welcome=1");
        await page.WaitForURLAsync(url => url.Contains("/mfa/verify"));
        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Verify')");
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == $"/ui/{c.Slug}/account?welcome=1");

        var resolved = await new SessionService(c.Store).ResolveAsync(c.InstanceId, await SessionCookieAsync(page, c));
        await Assert.That(resolved is not null && resolved.Value.User.Id == c.User.Id).IsTrue();
        await Assert.That((await c.Store.GetUserAsync(c.User.Id))!.LastSignInAt).IsNotNull();

        var events = await c.Store.ListAuditEventsAsync(c.InstanceId, 100);
        var signedIn = events.Single(e => e.Type == "user.signed_in");
        await Assert.That(signedIn.Data.TryGetValue("method", out var method) && method.ToString() == "password+mfa").IsTrue();
    }

    [Test]
    public async Task PasskeySignInSatisfiesTheStepUpForATotpUser()
    {
        var c = await ArrangeAsync("passkey");
        await EnrollAsync(c); // confirmed TOTP: the step-up gate is armed for this user

        // Password sign-in is intercepted by the MFA gate: pending redirect, no session cookie.
        using var password = await c.Wire.PostAsync(
            $"/ui/{c.Slug}/sign-in",
            Form(("email", c.User.Email!), ("password", "correct horse battery staple")));
        await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(password.Headers.Location?.ToString().Contains("mfa/verify")).IsTrue();
        await Assert.That(SessionCookie(password)).IsNull();

        // Register a passkey for the same user. The passkey ceremony itself is the hosted
        // page's backing JSON API, so it stays on the wire with the fake authenticator.
        using var authenticator = PasskeyE2eTests.NewAuthenticator(c.Service);
        string challenge;
        using (var options = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/passkeys/register/options")))
        {
            await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.OK);
            challenge = (await options.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("publicKey").GetProperty("challenge").GetString()!;
        }
        var register = SessionRequest(c, HttpMethod.Post, "/passkeys/register");
        register.Content = JsonContent.Create(new
        {
            id = authenticator.CredentialKey,
            nickname = "Step-up key",
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(
                    authenticator.ClientDataJson("webauthn.create", challenge))),
                attestationObject = Encodings.Base64UrlEncode(authenticator.AttestationObject()),
                transports = new[] { "internal" },
            },
        });
        using (var registered = await c.Http.SendAsync(register))
            await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The passkey ceremony alone mints a usable session — no TOTP is ever presented.
        using var assertOptions = await c.Http.PostAsync($"/ui/{c.Slug}/passkeys/assert/options", content: null);
        var assertChallenge = (await assertOptions.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicKey").GetProperty("challenge").GetString()!;
        var (authData, signature) = authenticator.Assertion(assertChallenge);
        using var verified = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passkeys/assert/verify", new
        {
            id = authenticator.CredentialKey,
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(
                    authenticator.ClientDataJson("webauthn.get", assertChallenge))),
                authenticatorData = Encodings.Base64UrlEncode(authData),
                signature = Encodings.Base64UrlEncode(signature),
            },
        });
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var cookie = SessionCookie(verified);
        await Assert.That(cookie).IsNotNull();

        using var accountRequest = new HttpRequestMessage(HttpMethod.Get, $"/ui/{c.Slug}/account");
        accountRequest.Headers.Add("Cookie", $"tsauth_session={cookie}");
        using var account = await c.Http.SendAsync(accountRequest);
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task VerifyRedirectTargetMustBeLocal()
    {
        var c = await ArrangeAsync("redirect");
        var (secret, _, _) = await EnrollAsync(c);
        await using var page = await BrowserFixture.NewPageAsync();

        // A hostile absolute next would navigate the browser off-origin if the guard
        // broke; the form's hidden next is tampered to carry it past sign-in's own
        // sanitizer so the verify POST's guard is the one under test.
        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/verify?token={token}"));
        await page.EvalOnSelectorAsync("input[name=next]", "el => el.value = 'https://evil.example/steal'");
        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Verify')");
        await page.WaitForURLAsync("**/account");
        await Assert.That(new Uri(page.Url).PathAndQuery).IsEqualTo($"/ui/{c.Slug}/account");

        // Protocol-relative targets are not site-local either.
        var (second, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/verify?token={second}"));
        await page.EvalOnSelectorAsync("input[name=next]", "el => el.value = '//evil.example/steal'");
        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Verify')");
        await page.WaitForURLAsync("**/account");
        await Assert.That(new Uri(page.Url).PathAndQuery).IsEqualTo($"/ui/{c.Slug}/account");
    }

    [Test]
    public async Task WrongVerifyCodeAllowsRetryWithSamePendingToken()
    {
        var c = await ArrangeAsync("retry");
        var (secret, _, _) = await EnrollAsync(c);
        await using var page = await BrowserFixture.NewPageAsync();

        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/verify?token={token}"));
        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow.AddMinutes(-10)));
        await page.ClickAsync("button:has-text('Verify')");
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("Invalid code");
        await Assert.That(await HasSessionCookieAsync(page, c)).IsFalse();

        // The same pending token accepts the retry: the browser follows to the account.
        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Verify')");
        await page.WaitForURLAsync("**/account");
        await Assert.That(await HasSessionCookieAsync(page, c)).IsTrue();
    }

    [Test]
    public async Task InvalidPendingTokenShowsExpiredPage()
    {
        var c = await ArrangeAsync("expired");
        await using var page = await BrowserFixture.NewPageAsync();
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/sign-in"));

        var post = page.WaitForResponseAsync(r => r.Url.EndsWith("/mfa/verify"));
        await SubmitFormAsync(page, c, $"/ui/{c.Slug}/mfa/verify",
            ("token", "not-a-real-token"), ("next", $"/ui/{c.Slug}/account"), ("code", "123456"));
        await Assert.That((await post).Status).IsEqualTo(200);
        await page.Locator("h1:has-text('expired')").WaitForAsync();
        await Assert.That(await page.ContentAsync()).Contains("/sign-in");
    }

    [Test]
    public async Task ExpiredPendingTokenIsRejected()
    {
        var c = await ArrangeAsync("ttl");
        var (secret, _, _) = await EnrollAsync(c);
        await using var page = await BrowserFixture.NewPageAsync();
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/sign-in"));

        // A pending token past its 10-minute TTL — wrong-code retries are allowed,
        // but only until the token expires.
        var token = SecureTokens.RandomToken(40);
        await c.Store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = c.InstanceId,
            UserId = c.User.Id,
            Purpose = "mfa.pending",
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-11).ToUnixTimeSeconds(),
        });

        var post = page.WaitForResponseAsync(r => r.Url.EndsWith("/mfa/verify"));
        await SubmitFormAsync(page, c, $"/ui/{c.Slug}/mfa/verify",
            ("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow)));
        await Assert.That((await post).Status).IsEqualTo(200);
        await page.Locator("h1:has-text('expired')").WaitForAsync();
        await Assert.That(await HasSessionCookieAsync(page, c)).IsFalse();
        await Assert.That(await SignedInCountAsync(c)).IsEqualTo(0);
    }

    [Test]
    public async Task PendingTokenIsSingleUseAfterSuccess()
    {
        var c = await ArrangeAsync("reuse");
        var (secret, _, _) = await EnrollAsync(c);
        await using var page = await BrowserFixture.NewPageAsync();

        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/verify?token={token}"));
        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Verify')");
        await page.WaitForURLAsync("**/account");
        // Exactly one session cookie in the browser's jar for the completed sign-in.
        var cookies = (await page.Context.CookiesAsync(c.Service.Origin.ToString()))
            .Where(cookie => cookie.Name == "tsauth_session").ToArray();
        await Assert.That(cookies.Length).IsEqualTo(1);

        // Replaying the consumed token shows the expired page and never replaces the session.
        var replay = page.WaitForResponseAsync(r => r.Url.EndsWith("/mfa/verify"));
        await SubmitFormAsync(page, c, $"/ui/{c.Slug}/mfa/verify",
            ("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow)));
        await Assert.That((await replay).Status).IsEqualTo(200);
        await page.Locator("h1:has-text('expired')").WaitForAsync();
        await Assert.That(await SessionCookieAsync(page, c)).IsEqualTo(cookies[0].Value);
        await Assert.That(await SignedInCountAsync(c)).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentVerifyWithOnePendingTokenMintsExactlyOneSession()
    {
        var c = await ArrangeAsync("race");
        var (secret, _, _) = await EnrollAsync(c);

        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        var code = Totp.Compute(secret, DateTimeOffset.UtcNow);
        var responses = await Task.WhenAll(
            c.Wire.PostAsync($"/ui/{c.Slug}/mfa/verify",
                Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", code))),
            c.Wire.PostAsync($"/ui/{c.Slug}/mfa/verify",
                Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", code))));

        using (responses[0])
        using (responses[1])
        {
            // The token is consumed atomically: whichever request loses the race sees
            // the expired page and never gets a session cookie.
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Found)).IsEqualTo(1);
            await Assert.That(responses.Count(r => SessionCookie(r) is not null)).IsEqualTo(1);
        }
        await Assert.That(await SignedInCountAsync(c)).IsEqualTo(1);
    }

    [Test]
    public async Task RecoveryCodeConsumedOnce()
    {
        var c = await ArrangeAsync("recovery");
        await using var page = await SessionPageAsync(c);
        var (_, codes, _) = await EnrollAsync(c, page);
        var arranged = await SessionCookieAsync(page, c);

        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/recovery"));
        await page.FillAsync("#code", codes[0]);
        await page.ClickAsync("button:has-text('Unlock account')");
        await page.WaitForURLAsync("**/account");
        // Consume minted a fresh session (a different token than the arranged one).
        var fresh = await SessionCookieAsync(page, c);
        await Assert.That(fresh).IsNotEqualTo(arranged);

        // The same code is burned: the page says so and the session is left untouched.
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/recovery"));
        await page.FillAsync("#code", codes[0]);
        await page.ClickAsync("button:has-text('Unlock account')");
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator(".error").TextContentAsync()).Contains("invalid or already used");
        await Assert.That(await SessionCookieAsync(page, c)).IsEqualTo(fresh);

        var recovery = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "recovery");
        await Assert.That(recovery.RecoveryCodes!.Length).IsEqualTo(9);
    }

    [Test]
    public async Task ManagementApiListsDeletesAndRegeneratesFactors()
    {
        var c = await ArrangeAsync("manage");
        var (_, originalCodes, factors) = await EnrollAsync(c);
        var totp = factors.Single(f => f.Type == "totp");
        var recovery = factors.Single(f => f.Type == "recovery");

        using (var unauthorized = await c.Http.GetAsync($"/v1/{c.Slug}/users/{c.User.Id}/mfa-factors"))
            await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using (var list = await c.Http.SendAsync(ApiRequest(c, HttpMethod.Get, $"/v1/{c.Slug}/users/{c.User.Id}/mfa-factors")))
        {
            await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await list.Content.ReadFromJsonAsync<JsonElement>();
            var data = body.GetProperty("data");
            await Assert.That(data.GetArrayLength()).IsEqualTo(2);
            // Never the secret or the hashed codes.
            await Assert.That(data.GetRawText().Contains("secret")).IsFalse();
            await Assert.That(data.GetRawText().Contains("recoveryCodes")).IsFalse();
        }

        using (var regenerateWrongType = await c.Http.SendAsync(
            ApiRequest(c, HttpMethod.Post, $"/v1/{c.Slug}/users/{c.User.Id}/mfa-factors/{totp.Id}/regenerate-recovery")))
            await Assert.That(regenerateWrongType.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string[] fresh;
        using (var regenerate = await c.Http.SendAsync(
            ApiRequest(c, HttpMethod.Post, $"/v1/{c.Slug}/users/{c.User.Id}/mfa-factors/{recovery.Id}/regenerate-recovery")))
        {
            await Assert.That(regenerate.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await regenerate.Content.ReadFromJsonAsync<JsonElement>();
            fresh = body.GetProperty("data").EnumerateArray().Select(v => v.GetString()!).ToArray();
        }
        await Assert.That(fresh.Length).IsEqualTo(10);

        // The pre-regeneration codes no longer work; the fresh ones do.
        using (var stale = await c.Wire.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/recovery/consume", Form(("code", originalCodes[0])))))
        {
            var html = await stale.Content.ReadAsStringAsync();
            await Assert.That(html.Contains("invalid or already used")).IsTrue();
        }
        using var freshWorks = await c.Wire.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/recovery/consume", Form(("code", fresh[0]))));
        await Assert.That(freshWorks.StatusCode).IsEqualTo(HttpStatusCode.Found);

        using (var deleted = await c.Http.SendAsync(ApiRequest(c, HttpMethod.Delete, $"/v1/{c.Slug}/users/{c.User.Id}/mfa-factors/{totp.Id}")))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using (var afterDelete = await c.Http.SendAsync(ApiRequest(c, HttpMethod.Get, $"/v1/{c.Slug}/users/{c.User.Id}/mfa-factors")))
        {
            var body = await afterDelete.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("data").GetArrayLength()).IsEqualTo(1);
        }

        var events = await c.Store.ListAuditEventsAsync(c.InstanceId, 100);
        await Assert.That(events.Any(e => e.Type == "mfa.factor_deleted")).IsTrue();
    }

    // ---- Arranging -------------------------------------------------------------------

    private sealed record Context(
        AuthServiceFixture Service,
        string InstanceId,
        string Slug,
        string ApiKey,
        User User,
        string SessionToken,
        HttpClient Wire)
    {
        public AuthStore Store => Service.Store;

        public HttpClient Http => Service.Http;
    }

    private static async Task<Context> ArrangeAsync(string label)
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync($"mfa-{label}-{Guid.NewGuid().ToString("N")[..6]}");

        var sessions = new SessionService(service.Store);
        var user = await sessions.CreateUserAsync(instanceId, $"{label}-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, "correct horse battery staple", null);
        var (_, token) = await sessions.CreateSessionAsync(instanceId, user.Id, "e2e", "127.0.0.1");
        // The fixture's shared client follows redirects; the wire tests here assert
        // 302s and Location headers, so they send through one that does not.
        var wire = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = service.Origin };
        return new Context(service, instanceId, slug, apiKey, user, token, wire);
    }

    /// <summary>Runs the full happy-path enrollment through the hosted pages (the
    /// start form, the confirm form) and returns the secret, the recovery codes as
    /// shown on the page, and the final factor list.</summary>
    private static async Task<(byte[] Secret, string[] Codes, IReadOnlyList<MfaFactor> Factors)> EnrollAsync(Context c, IPage page)
    {
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/mfa/enroll"));
        await page.ClickAsync("button:has-text('Set up authenticator')");

        var totp = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);

        await page.FillAsync("#code", Totp.Compute(secret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Confirm')");
        await page.Locator(".list .row code").First.WaitForAsync();
        var codes = (await page.Locator(".list .row code").AllTextContentsAsync()).ToArray();

        return (secret, codes, await c.Store.ListMfaFactorsAsync(c.User.Id));
    }

    /// <summary>Enrollment as pure arrangement on a throwaway browser page.</summary>
    private static async Task<(byte[] Secret, string[] Codes, IReadOnlyList<MfaFactor> Factors)> EnrollAsync(Context c)
    {
        await using var page = await SessionPageAsync(c);
        return await EnrollAsync(c, page);
    }

    private static HttpRequestMessage SessionRequest(Context c, HttpMethod method, string path, FormUrlEncodedContent? content = null)
    {
        var message = new HttpRequestMessage(method, $"/ui/{c.Slug}{path}") { Content = content };
        message.Headers.Add("Cookie", $"tsauth_session={c.SessionToken}");
        return message;
    }

    private static HttpRequestMessage ApiRequest(Context c, HttpMethod method, string path) => new(method, path)
    {
        Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", c.ApiKey) },
    };

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static string? SessionCookie(HttpResponseMessage response)
    {
        var header = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("tsauth_session=", StringComparison.Ordinal))
            : null;
        return header?["tsauth_session=".Length..].Split(';')[0];
    }

    /// <summary>Completed password+mfa sign-ins for the arranged user's instance, as audit events.</summary>
    private static async Task<int> SignedInCountAsync(Context c) =>
        (await c.Store.ListAuditEventsAsync(c.InstanceId, 100)).Count(e => e.Type == "user.signed_in");

    // ---- Browser helpers ----------------------------------------------------------------

    private static string PageUrl(Context c, string path) => new Uri(c.Service.Origin, path).ToString();

    /// <summary>A fresh browser page signed in as the arranged user: the session token
    /// is fixture state, every interaction after that is the real hosted flow.</summary>
    private static async Task<IPage> SessionPageAsync(Context c)
    {
        var page = await BrowserFixture.NewPageAsync();
        await page.Context.AddCookiesAsync([new Microsoft.Playwright.Cookie
        {
            Name = "tsauth_session",
            Value = c.SessionToken,
            Url = c.Service.Origin.ToString(),
        }]);
        return page;
    }

    /// <summary>Submits the sign-in form with the arranged user's password; the browser
    /// follows whatever redirect the service returns (direct session or MFA gate).</summary>
    private static async Task SignInAsync(IPage page, Context c, string? next = null)
    {
        var query = next is null ? "" : $"?next={Uri.EscapeDataString(next)}";
        await page.GotoAsync(PageUrl(c, $"/ui/{c.Slug}/sign-in{query}"));
        await page.FillAsync("#si-email", c.User.Email!);
        await page.FillAsync("#si-password", "correct horse battery staple");
        await page.ClickAsync("button:has-text('Sign in')");
    }

    /// <summary>Posts like a stale or script-built browser form would (real navigation,
    /// no client-side validation) — for fields no rendered form can carry anymore,
    /// which is exactly the replay scenario under test.</summary>
    private static async Task SubmitFormAsync(IPage page, Context c, string path, params (string Name, string Value)[] fields)
    {
        var inputs = string.Concat(fields.Select(f => $"<input type=\"hidden\" name=\"{f.Name}\" value=\"{f.Value}\">"));
        var form = $"<form id=\"e2e-post\" method=\"post\" action=\"{PageUrl(c, path)}\">{inputs}</form>";
        await page.EvalOnSelectorAsync("body",
            "(body, html) => { body.insertAdjacentHTML('beforeend', html); body.querySelector('#e2e-post').submit(); }",
            form);
    }

    private static async Task<bool> HasSessionCookieAsync(IPage page, Context c) =>
        (await page.Context.CookiesAsync(c.Service.Origin.ToString())).Any(cookie => cookie.Name == "tsauth_session");

    private static async Task<string> SessionCookieAsync(IPage page, Context c) =>
        (await page.Context.CookiesAsync(c.Service.Origin.ToString()))
            .Single(cookie => cookie.Name == "tsauth_session").Value;
}
