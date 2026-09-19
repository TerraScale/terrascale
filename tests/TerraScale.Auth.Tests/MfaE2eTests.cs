namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>TOTP enrollment, the sign-in second step, recovery codes, and the MFA management API.</summary>
[NotInParallel]
public sealed class MfaE2eTests
{
    private static readonly Regex RecoveryCodePattern = new("<code>([A-Za-z0-9]{10})</code>");

    [Test]
    public async Task EnrollPageRequiresSession()
    {
        var c = await ArrangeAsync("page");
        var response = await c.Http.GetAsync($"/ui/{c.Slug}/mfa/enroll");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(response.Headers.Location?.ToString().Contains("/sign-in")).IsTrue();
    }

    [Test]
    public async Task EnrollmentShowsSecretAndIssuesRecoveryCodes()
    {
        var c = await ArrangeAsync("enroll");
        var (secret, codes, factors) = await EnrollAsync(c);

        await Assert.That(Encodings.Base32Encode(secret).Length).IsEqualTo(32);
        await Assert.That(codes.Length).IsEqualTo(10);

        var totp = factors.Single(f => f.Type == "totp");
        await Assert.That(totp.Confirmed).IsTrue();

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
        using (var start = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/enroll/start")))
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var totp = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);

        // A code from 10 minutes ago is far outside the ±30s verify window.
        var stale = Totp.Compute(secret, DateTimeOffset.UtcNow.AddMinutes(-10));
        using (var wrong = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post,
            $"/mfa/enroll/confirm?factor={totp.Id}", Form(("code", stale)))))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await wrong.Content.ReadAsStringAsync()).Contains("Try again")).IsTrue();
        }
        await Assert.That((await c.Store.GetMfaFactorAsync(totp.Id))!.Confirmed).IsFalse();

        using var right = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post,
            $"/mfa/enroll/confirm?factor={totp.Id}", Form(("code", Totp.Compute(secret, DateTimeOffset.UtcNow)))));
        await Assert.That(right.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await c.Store.GetMfaFactorAsync(totp.Id))!.Confirmed).IsTrue();
    }

    [Test]
    public async Task UnconfirmedFactorDoesNotGateSignInOrVerifyCodes()
    {
        var c = await ArrangeAsync("unconfirmed");
        using (var start = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/enroll/start")))
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var totp = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);

        // Password sign-in issues a session directly: an unconfirmed factor never routes to the MFA gate.
        using var signIn = await c.Http.PostAsync(
            $"/ui/{c.Slug}/sign-in",
            Form(("email", c.User.Email!), ("password", "correct horse battery staple")));
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(signIn.Headers.Location?.ToString().Contains("mfa/verify")).IsFalse();
        await Assert.That(SessionCookie(signIn)).IsNotNull();

        // The not-yet-confirmed secret never verifies at the MFA gate either.
        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        using var verify = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow))));
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await verify.Content.ReadAsStringAsync()).Contains("Invalid code")).IsTrue();
    }

    [Test]
    public async Task ConfirmingAnAlreadyConfirmedFactorIsRejected()
    {
        var c = await ArrangeAsync("reconfirm");
        var (secret, codes, factors) = await EnrollAsync(c);
        var totp = factors.Single(f => f.Type == "totp");

        using var replay = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post,
            $"/mfa/enroll/confirm?factor={totp.Id}", Form(("code", Totp.Compute(secret, DateTimeOffset.UtcNow)))));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The already-saved recovery set was not silently re-minted.
        var recovery = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "recovery");
        await Assert.That(recovery.RecoveryCodes!.Contains(SecureTokens.Sha256Hex(codes[0]))).IsTrue();
    }

    [Test]
    public async Task SecondConfirmedTotpEnrollmentIsRejected()
    {
        var c = await ArrangeAsync("dupe");
        await EnrollAsync(c);

        using var again = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/enroll/start"));
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await again.Content.ReadAsStringAsync()).Contains("already have a confirmed authenticator")).IsTrue();
        await Assert.That((await c.Store.ListMfaFactorsAsync(c.User.Id)).Count(f => f.Type == "totp")).IsEqualTo(1);
    }

    [Test]
    public async Task VerifyWithCorrectCodeCreatesSessionAndRedirects()
    {
        var c = await ArrangeAsync("verify");
        var (secret, _, _) = await EnrollAsync(c);

        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        using (var form = await c.Http.GetAsync($"/ui/{c.Slug}/mfa/verify?token={token}"))
            await Assert.That(form.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var post = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", $"/ui/{c.Slug}/account?welcome=1"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow))));
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(post.Headers.Location?.ToString()).IsEqualTo($"/ui/{c.Slug}/account?welcome=1");

        var resolved = await new SessionService(c.Store).ResolveAsync(c.InstanceId, SessionCookie(post)!);
        await Assert.That(resolved is not null && resolved.Value.User.Id == c.User.Id).IsTrue();
        await Assert.That((await c.Store.GetUserAsync(c.User.Id))!.LastSignInAt).IsNotNull();

        var events = await c.Store.ListAuditEventsAsync(c.InstanceId, 100);
        var signedIn = events.Single(e => e.Type == "user.signed_in");
        await Assert.That(signedIn.Data.TryGetValue("method", out var method) && method.ToString() == "password+mfa").IsTrue();
    }

    [Test]
    public async Task VerifyRedirectTargetMustBeLocal()
    {
        var c = await ArrangeAsync("redirect");
        var (secret, _, _) = await EnrollAsync(c);

        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        using var post = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", "https://evil.example/steal"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow))));
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(post.Headers.Location?.ToString()).IsEqualTo($"/ui/{c.Slug}/account");

        // Protocol-relative targets are not site-local either.
        var (second, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        using var relative = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", second), ("next", "//evil.example/steal"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow))));
        await Assert.That(relative.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(relative.Headers.Location?.ToString()).IsEqualTo($"/ui/{c.Slug}/account");
    }

    [Test]
    public async Task WrongVerifyCodeAllowsRetryWithSamePendingToken()
    {
        var c = await ArrangeAsync("retry");
        var (secret, _, _) = await EnrollAsync(c);

        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        using (var wrong = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow.AddMinutes(-10))))))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await wrong.Content.ReadAsStringAsync()).Contains("Invalid code")).IsTrue();
            await Assert.That(wrong.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();
        }

        using var right = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow))));
        await Assert.That(right.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(SessionCookie(right)).IsNotNull();
    }

    [Test]
    public async Task InvalidPendingTokenShowsExpiredPage()
    {
        var c = await ArrangeAsync("expired");
        using var post = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", "not-a-real-token"), ("next", $"/ui/{c.Slug}/account"), ("code", "123456")));
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await post.Content.ReadAsStringAsync();
        await Assert.That(html.Contains("expired")).IsTrue();
        await Assert.That(html.Contains("/sign-in")).IsTrue();
    }

    [Test]
    public async Task ExpiredPendingTokenIsRejected()
    {
        var c = await ArrangeAsync("ttl");
        var (secret, _, _) = await EnrollAsync(c);

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

        using var post = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow))));
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await post.Content.ReadAsStringAsync()).Contains("expired")).IsTrue();
        await Assert.That(post.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();
        await Assert.That(await SignedInCountAsync(c)).IsEqualTo(0);
    }

    [Test]
    public async Task PendingTokenIsSingleUseAfterSuccess()
    {
        var c = await ArrangeAsync("reuse");
        var (secret, _, _) = await EnrollAsync(c);

        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        using (var post = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow)))))
        {
            await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.Found);
            // Exactly one session cookie on the response that completes the sign-in.
            await Assert.That(post.Headers.GetValues("Set-Cookie").Count(v => v.StartsWith("tsauth_session=", StringComparison.Ordinal))).IsEqualTo(1);
        }

        using var replay = await c.Http.PostAsync(
            $"/ui/{c.Slug}/mfa/verify",
            Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", Totp.Compute(secret, DateTimeOffset.UtcNow))));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await replay.Content.ReadAsStringAsync()).Contains("expired")).IsTrue();
        await Assert.That(replay.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();
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
            c.Http.PostAsync($"/ui/{c.Slug}/mfa/verify",
                Form(("token", token), ("next", $"/ui/{c.Slug}/account"), ("code", code))),
            c.Http.PostAsync($"/ui/{c.Slug}/mfa/verify",
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
        var (_, codes, _) = await EnrollAsync(c);

        using (var first = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/recovery/consume", Form(("code", codes[0])))))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Found);
            await Assert.That(SessionCookie(first)).IsNotNull();
        }

        using var second = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/recovery/consume", Form(("code", codes[0]))));
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await second.Content.ReadAsStringAsync()).Contains("invalid or already used")).IsTrue();

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
        using (var stale = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/recovery/consume", Form(("code", originalCodes[0])))))
        {
            var html = await stale.Content.ReadAsStringAsync();
            await Assert.That(html.Contains("invalid or already used")).IsTrue();
        }
        using var freshWorks = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/recovery/consume", Form(("code", fresh[0]))));
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
        string SessionToken)
    {
        public AuthStore Store => Service.Store;

        public HttpClient Http => Service.Http;
    }

    private static async Task<Context> ArrangeAsync(string label)
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync($"mfa-{label}-{Guid.NewGuid().ToString("N")[..6]}");

        // InstanceResolver hashes the full bearer token, but the operator endpoint
        // stores sha256 of only the secret half of the sk_ key it returns — arrange a
        // key whose stored hash matches the resolver as written.
        var apiKey = "sk_" + SecureTokens.RandomToken(40);
        await service.Store.CreateApiKeyAsync(new ApiKey
        {
            Id = "key_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Name = "e2e",
            SecretKey = SecureTokens.Sha256Hex(apiKey),
            Scopes = ["*"],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var sessions = new SessionService(service.Store);
        var user = await sessions.CreateUserAsync(instanceId, $"{label}-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, "correct horse battery staple", null);
        var (_, token) = await sessions.CreateSessionAsync(instanceId, user.Id, "e2e", "127.0.0.1");
        return new Context(service, instanceId, slug, apiKey, user, token);
    }

    /// <summary>Runs the full happy-path enrollment and returns the secret, the plaintext recovery codes, and the final factor list.</summary>
    private static async Task<(byte[] Secret, string[] Codes, IReadOnlyList<MfaFactor> Factors)> EnrollAsync(Context c)
    {
        using (var start = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post, "/mfa/enroll/start")))
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var totp = (await c.Store.ListMfaFactorsAsync(c.User.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);

        using var confirm = await c.Http.SendAsync(SessionRequest(c, HttpMethod.Post,
            $"/mfa/enroll/confirm?factor={totp.Id}", Form(("code", Totp.Compute(secret, DateTimeOffset.UtcNow)))));
        await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await confirm.Content.ReadAsStringAsync();
        var codes = RecoveryCodePattern.Matches(html).Select(m => m.Groups[1].Value).ToArray();

        return (secret, codes, await c.Store.ListMfaFactorsAsync(c.User.Id));
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
}
