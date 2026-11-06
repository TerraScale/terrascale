using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OtpNet;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Identity;

/// <summary>Hosted authenticator enrolment, two-factor sign-in, recovery codes and the organization policy.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class MfaSignInTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_user_enrols_signs_in_with_the_second_factor_and_turns_it_off()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("carol@example.test");
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);

        var (secret, codes, _) = await EnrolAsync(oidc, session.Token, clock);
        await Assert.That(codes.Count).IsEqualTo(8);
        using (var old = await SendAsync(oidc, HttpMethod.Get, "/ui/terrascale/security", session.Token))
        {
            // Activation signed every older session out.
            await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        }

        // The emailed code alone opens nothing; a wrong second factor is refused.
        clock.UtcNow += HostedAuth.MailCooldown;
        var challenge = await ChallengeAsync(oidc, user.Email);
        using (var wrong = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = WrongCode(secret, clock) }))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();
        }

        string signedIn;
        using (var right = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = Code(secret, clock) }))
        {
            await Assert.That(right.StatusCode).IsEqualTo(HttpStatusCode.OK);
            signedIn = OidcHarness.SessionCookie(right)!;
            await Assert.That(signedIn).IsNotNull();
        }

        // Tokens from a two-factor session carry the proof and keep working through refresh and the platform API.
        var (verifier, pkce) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(signedIn, pkce), verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        var access = tokens.GetProperty("access_token").GetString()!;
        await Assert.That(new JsonWebToken(access).GetClaim(OidcIssuer.MfaClaim).Value).IsEqualTo("totp");
        using (var userInfo = await oidc.UserInfoAsync(access))
        {
            await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var refresh = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var refreshed = (await OidcHarness.JsonAsync(refresh)).GetProperty("access_token").GetString()!;
            await Assert.That(new JsonWebToken(refreshed).GetClaim(OidcIssuer.MfaClaim).Value).IsEqualTo("totp");
        }

        var status = await oidc.OkAsync(HttpMethod.Get, "/v1/terrascale/account/mfa", access);
        await Assert.That(status.GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(status.GetProperty("recovery_codes_remaining").GetInt32()).IsEqualTo(8);

        // Turning it off needs a code and signs everything out; the next sign-in needs only the email code.
        clock.UtcNow += TimeSpan.FromSeconds(31);
        using (var disable = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/totp/disable", signedIn, new() { ["code"] = Code(secret, clock) }))
        {
            await Assert.That(disable.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await disable.Content.ReadAsStringAsync()).Contains("Two-factor authentication is off.");
            await Assert.That(OidcHarness.SessionCookie(disable)).IsNotNull();
        }

        using (var stale = await oidc.UserInfoAsync(access))
        {
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        await Assert.That(await oidc.SignInAsync(user.Email)).IsNotEmpty();
    }

    [Test]
    public async Task A_recovery_code_signs_in_once_and_replay_is_refused()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("recovery@example.test");
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var (_, codes, _) = await EnrolAsync(oidc, session.Token, clock);

        // A recovery code works once.
        clock.UtcNow += HostedAuth.MailCooldown;
        using (var recovery = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = await ChallengeAsync(oidc, user.Email), ["code"] = codes[0] }))
        {
            await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(OidcHarness.SessionCookie(recovery)).IsNotNull();
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        using (var replay = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = await ChallengeAsync(oidc, user.Email), ["code"] = codes[0] }))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(replay)).IsNull();
        }
    }

    [Test]
    public async Task A_user_regenerates_recovery_codes_and_the_old_set_stops_working()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("rotate@example.test");
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var (secret, oldCodes, enrolled) = await EnrolAsync(oidc, session.Token, clock);
        clock.UtcNow += TimeSpan.FromSeconds(31);

        // A recovery code of the old set reauthenticates the rotation; the same session stays live.
        string rotated;
        using (var rotate = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/recovery-codes", enrolled, new() { ["code"] = oldCodes[0] }))
        {
            await Assert.That(rotate.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await rotate.Content.ReadAsStringAsync();
            var fresh = RecoveryPattern().Matches(body).Select(match => match.Groups[1].Value).ToList();
            await Assert.That(fresh.Count).IsEqualTo(8);
            await Assert.That(fresh).DoesNotContain(oldCodes[1]);
            await Assert.That(body).Contains("They expire ");
            rotated = fresh[0];
        }

        var status = await StatusAsync(oidc, enrolled);
        await Assert.That(status.Enabled).IsTrue();
        await Assert.That(status.Remaining).IsEqualTo(8);

        // The new set signs in once; every old code is refused.
        clock.UtcNow += HostedAuth.MailCooldown;
        using (var signIn = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = await ChallengeAsync(oidc, user.Email), ["code"] = rotated }))
        {
            await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(OidcHarness.SessionCookie(signIn)).IsNotNull();
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        using (var stale = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = await ChallengeAsync(oidc, user.Email), ["code"] = oldCodes[1] }))
        {
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(stale)).IsNull();
        }
    }

    [Test]
    public async Task Recovery_code_regeneration_refuses_a_wrong_or_replayed_proof()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("rotate-refused@example.test");
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var (secret, codes, enrolled) = await EnrolAsync(oidc, session.Token, clock);
        clock.UtcNow += TimeSpan.FromSeconds(31);

        // A wrong (out-of-window) TOTP proof changes nothing.
        using (var wrong = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/recovery-codes", enrolled, new() { ["code"] = WrongCode(secret, clock) }))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        // A replayed recovery proof is refused; the code it tried to spend still works once afterwards.
        using (var first = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/recovery-codes", enrolled, new() { ["code"] = codes[0] }))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var replay = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/recovery-codes", enrolled, new() { ["code"] = codes[0] }))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(replay)).IsNull();
        }

        // The committed rotation replaced the set: an old-set code no longer reauthenticates anything.
        using (var stale = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/recovery-codes", enrolled, new() { ["code"] = codes[1] }))
        {
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        var status = await StatusAsync(oidc, enrolled);
        await Assert.That(status.Enabled).IsTrue();
        await Assert.That(status.Remaining).IsEqualTo(8);
    }

    /// <summary>Enrols through the hosted security pages; returns the secret, the recovery codes and the new session.</summary>
    private static async Task<(string Secret, IReadOnlyList<string> Codes, string Session)> EnrolAsync(OidcHarness oidc, string session, StoreTestClock clock)
    {
        using var begin = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/totp", session, []);
        await Assert.That(begin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var setup = await begin.Content.ReadAsStringAsync();
        await Assert.That(setup).Contains("src=\"data:image/png;base64,");
        var secret = SecretPattern().Match(setup).Groups[1].Value;
        using var confirm = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/totp/confirm", session, new() { ["code"] = Code(secret, clock) });
        await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var codes = RecoveryPattern().Matches(await confirm.Content.ReadAsStringAsync()).Select(match => match.Groups[1].Value).ToList();
        var enrolled = OidcHarness.SessionCookie(confirm)!;
        await Assert.That(enrolled).IsNotNull();
        clock.UtcNow += TimeSpan.FromSeconds(31);
        return (secret, codes, enrolled);
    }

    /// <summary>Signs in with an emailed code and returns the second-factor challenge the page carries.</summary>
    private static async Task<string> ChallengeAsync(OidcHarness oidc, string email)
    {
        using (var start = await oidc.PostFormAsync("/ui/terrascale/sign-in", new() { ["email"] = email }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var verify = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new() { ["email"] = email, ["code"] = await oidc.Email.NextCodeAsync(email) });
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(verify)).IsNull();
        return WebUtility.HtmlDecode(ChallengePattern().Match(await verify.Content.ReadAsStringAsync()).Groups[1].Value);
    }

    private static Task<HttpResponseMessage> SendAsync(OidcHarness oidc, HttpMethod method, string path, string session, Dictionary<string, string>? form = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        if (form is not null)
        {
            request.Headers.Add("Origin", OidcHarness.PublicOrigin);
            request.Content = new FormUrlEncodedContent(form);
        }

        return oidc.Client.SendAsync(request);
    }

    private static string Code(string secret, StoreTestClock clock) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.UtcNow.UtcDateTime);

    private static async Task<(bool Enabled, int Remaining)> StatusAsync(OidcHarness oidc, string session)
    {
        using var page = await SendAsync(oidc, HttpMethod.Get, "/ui/terrascale/security", session);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await page.Content.ReadAsStringAsync();
        return (body.Contains("On (authenticator app)"), int.Parse(RemainingPattern().Match(body).Groups[1].Value, CultureInfo.InvariantCulture));
    }

    // A code from an hour away is outside the one-step verification window.
    private static string WrongCode(string secret, StoreTestClock clock) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.UtcNow.UtcDateTime.AddHours(1));

    [GeneratedRegex("<dt>Setup key</dt><dd><code>([A-Z2-7]+)</code>")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("<dt>Recovery codes left</dt><dd>([0-9]+)</dd>")]
    private static partial Regex RemainingPattern();

    [GeneratedRegex("<li><code>([A-Za-z0-9]{24})</code></li>")]
    private static partial Regex RecoveryPattern();

    [GeneratedRegex("name=\"challenge\" value=\"([^\"]+)\"")]
    private static partial Regex ChallengePattern();
}
