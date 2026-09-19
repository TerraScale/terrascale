namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Email passcode sign-in: request, verify, throttling, lockout, the MFA
/// second step, and the hosted pages.</summary>
[NotInParallel]
public sealed class PasscodeE2eTests
{
    private sealed record Context(AuthServiceFixture Service, string InstanceId, string Slug, User User)
    {
        public AuthStore Store => Service.Store;

        public HttpClient Http => Service.Http;
    }

    private static async Task<Context> ArrangeAsync(string label)
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("passcode-" + label + "-" + Guid.NewGuid().ToString("N")[..6]);
        var user = await new SessionService(service.Store).CreateUserAsync(
            instanceId, $"{label}-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, null, null);
        return new Context(service, instanceId, slug, user);
    }

    [Test]
    public async Task RequestStoresTokenAndDoesNotEnumerateAccounts()
    {
        var c = await ArrangeAsync("request");

        using (var known = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/request", new { email = c.User.Email }))
        {
            await Assert.That(known.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await known.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();
        }
        using (var unknown = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/request", new { email = "nobody@nowhere.example" }))
        {
            await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();
        }

        var token = await PasscodeTokenAsync(c.Store, c.InstanceId, c.User.Id);
        await Assert.That(token).IsNotNull();
        await Assert.That(token!.Purpose).IsEqualTo("passcode");
        await Assert.That(token.UserId).IsEqualTo(c.User.Id);
        await Assert.That(token.Consumed).IsFalse();
        await Assert.That(token.ExpiresAt).IsGreaterThanOrEqualTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 590);
    }

    [Test]
    public async Task RequestIsThrottledPerUserWithin30Seconds()
    {
        var c = await ArrangeAsync("throttle");
        var other = await new SessionService(c.Store).CreateUserAsync(
            c.InstanceId, $"other-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, null, null);

        foreach (var _ in Enumerable.Range(0, 2))
            using (var response = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/request", new { email = c.User.Email }))
                await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();
        using (var notThrottled = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/request", new { email = other.Email }))
            await Assert.That((await notThrottled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();

        // The second request for the same user was swallowed; the other user got one.
        await Assert.That((await AllPasscodeTokensAsync(c.Store, c.InstanceId, c.User.Id)).Count).IsEqualTo(1);
        await Assert.That((await AllPasscodeTokensAsync(c.Store, c.InstanceId, other.Id)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task VerifyWithKnownCodeCreatesSessionAndAudits()
    {
        var c = await ArrangeAsync("verify");
        await MintPasscodeAsync(c.Store, c.InstanceId, c.User.Id, "314159");

        using var verified = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/verify", new { email = c.User.Email, code = "314159" });
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();

        var resolved = await new SessionService(c.Store).ResolveAsync(c.InstanceId, SessionCookie(verified)!);
        await Assert.That(resolved is not null && resolved.Value.User.Id == c.User.Id).IsTrue();
        await Assert.That((await c.Store.GetUserAsync(c.User.Id))!.LastSignInAt).IsNotNull();

        var signedIn = (await c.Store.ListAuditEventsAsync(c.InstanceId, 100)).Single(e => e.Type == "user.signed_in");
        await Assert.That(signedIn.Data.TryGetValue("method", out var method) && method.ToString() == "passcode").IsTrue();

        // Codes are single use.
        using (var replay = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/verify", new { email = c.User.Email, code = "314159" }))
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task WrongCodeAllowsRetryButFiveFailuresKillTheToken()
    {
        var c = await ArrangeAsync("lockout");

        // One typo does not lock the user out.
        await MintPasscodeAsync(c.Store, c.InstanceId, c.User.Id, "654321");
        using (var typo = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/verify", new { email = c.User.Email, code = "000000" }))
            await Assert.That(typo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using (var correct = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/verify", new { email = c.User.Email, code = "654321" }))
            await Assert.That(correct.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Five failures on a fresh token burn it.
        await MintPasscodeAsync(c.Store, c.InstanceId, c.User.Id, "111222");
        foreach (var _ in Enumerable.Range(0, 5))
            using (var wrong = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/verify", new { email = c.User.Email, code = "999999" }))
            {
                await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                await Assert.That((await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_code");
            }
        await Assert.That((await PasscodeTokenAsync(c.Store, c.InstanceId, c.User.Id))!.Consumed).IsTrue();
        using (var dead = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/verify", new { email = c.User.Email, code = "111222" }))
            await Assert.That(dead.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task MfaUserStepsUpWithTotpBeforeTheSessionIsIssued()
    {
        var c = await ArrangeAsync("mfa");
        var secret = RandomNumberGenerator.GetBytes(20);
        await c.Store.CreateMfaFactorAsync(new MfaFactor
        {
            Id = "mfa_" + SecureTokens.RandomToken(20),
            InstanceId = c.InstanceId,
            UserId = c.User.Id,
            Type = "totp",
            SecretBox = SecretBox.Encrypt(AuthServiceFixture.MasterKey, secret),
            Confirmed = true,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await MintPasscodeAsync(c.Store, c.InstanceId, c.User.Id, "246810");

        string mfaToken;
        using (var verified = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/verify", new { email = c.User.Email, code = "246810" }))
        {
            await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await verified.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("mfa_required").GetBoolean()).IsTrue();
            mfaToken = body.GetProperty("mfa_token").GetString()!;
            await Assert.That(verified.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();
        }
        await Assert.That((await c.Store.ListAuditEventsAsync(c.InstanceId, 100)).Any(e => e.Type == "user.signed_in")).IsFalse();

        using (var wrong = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/mfa", new { mfa_token = mfaToken, code = "000000" }))
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        string cookie;
        using (var right = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/mfa", new { mfa_token = mfaToken, code = Totp.Compute(secret, DateTimeOffset.UtcNow) }))
        {
            await Assert.That(right.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await right.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();
            cookie = SessionCookie(right)!;
        }
        var resolved = await new SessionService(c.Store).ResolveAsync(c.InstanceId, cookie);
        await Assert.That(resolved is not null && resolved.Value.User.Id == c.User.Id).IsTrue();
        var signedIn = (await c.Store.ListAuditEventsAsync(c.InstanceId, 100)).Single(e => e.Type == "user.signed_in");
        await Assert.That(signedIn.Data.TryGetValue("method", out var method) && method.ToString() == "passcode+mfa").IsTrue();

        // The pending token is single use.
        using (var replay = await c.Http.PostAsJsonAsync($"/ui/{c.Slug}/passcode/mfa", new { mfa_token = mfaToken, code = Totp.Compute(secret, DateTimeOffset.UtcNow) }))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_mfa_token");
        }
    }

    [Test]
    public async Task HostedPagesRender()
    {
        var c = await ArrangeAsync("pages");

        using (var page = await c.Http.GetAsync($"/ui/{c.Slug}/passcode"))
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await page.Content.ReadAsStringAsync();
            await Assert.That(html.Contains("/passcode/request")).IsTrue();
            await Assert.That(html.Contains("/passcode/verify")).IsTrue();
            await Assert.That(html.Contains($"/ui/{c.Slug}/sign-in")).IsTrue();
        }
        using (var expired = await c.Http.GetAsync($"/ui/{c.Slug}/passcode/mfa?token=garbage"))
        {
            await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await expired.Content.ReadAsStringAsync()).Contains("expired")).IsTrue();
        }

        // A live pending token renders the form, and the form posts through to a redirect.
        var secret = RandomNumberGenerator.GetBytes(20);
        await c.Store.CreateMfaFactorAsync(new MfaFactor
        {
            Id = "mfa_" + SecureTokens.RandomToken(20),
            InstanceId = c.InstanceId,
            UserId = c.User.Id,
            Type = "totp",
            SecretBox = SecretBox.Encrypt(AuthServiceFixture.MasterKey, secret),
            Confirmed = true,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        var (token, _) = await MfaGate.BeginPendingAsync(c.Store, c.InstanceId, c.User.Id);
        using (var form = await c.Http.GetAsync($"/ui/{c.Slug}/passcode/mfa?token={token}"))
        {
            var html = await form.Content.ReadAsStringAsync();
            await Assert.That(html.Contains($"name=\"mfa_token\" value=\"{token}\"")).IsTrue();
        }
        using var posted = await c.Http.PostAsync($"/ui/{c.Slug}/passcode/mfa",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["mfa_token"] = token, ["code"] = Totp.Compute(secret, DateTimeOffset.UtcNow) }));
        await Assert.That(posted.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(posted.Headers.Location?.ToString()).IsEqualTo($"/ui/{c.Slug}/account");
    }

    // ---- Helpers ---------------------------------------------------------------------

    /// <summary>Stores a passcode verification token with a code the test knows.</summary>
    private static async Task MintPasscodeAsync(AuthStore store, string instanceId, string userId, string code)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vtok_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            Purpose = "passcode",
            TokenKey = SecureTokens.Sha256Hex(code),
            ExpiresAt = now + 600,
            CreatedAt = now,
        });
    }

    private static async Task<IReadOnlyList<VerificationToken>> AllPasscodeTokensAsync(AuthStore store, string instanceId, string userId)
    {
        var tokens = await store.ListVerificationTokensAsync(instanceId, "passcode", 10, default);
        return tokens.Where(t => t.UserId == userId).ToList();
    }

    private static async Task<VerificationToken?> PasscodeTokenAsync(AuthStore store, string instanceId, string userId) =>
        (await AllPasscodeTokensAsync(store, instanceId, userId)).OrderByDescending(t => t.CreatedAt).FirstOrDefault();

    private static string? SessionCookie(HttpResponseMessage response)
    {
        var header = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("tsauth_session=", StringComparison.Ordinal))
            : null;
        return header?["tsauth_session=".Length..].Split(';')[0];
    }
}
