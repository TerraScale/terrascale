using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OtpNet;
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
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    public async Task A_user_enrols_signs_in_with_the_second_factor_uses_a_recovery_code_once_and_turns_it_off()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("carol@example.test");
        var session = await oidc.SignInAsync(user.Email);

        using (var page = await SendAsync(oidc, HttpMethod.Get, "/ui/terrascale/security", session))
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Set up an authenticator app");
        }

        var (secret, codes, enrolled) = await EnrolAsync(oidc, session, clock);
        await Assert.That(codes.Count).IsEqualTo(8);
        using (var old = await SendAsync(oidc, HttpMethod.Get, "/ui/terrascale/security", session))
        {
            // Activation signed every older session out.
            await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        }

        using (var page = await SendAsync(oidc, HttpMethod.Get, "/ui/terrascale/security", enrolled))
        {
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("On (authenticator app)");
        }

        // The emailed code alone opens nothing; a wrong second factor is refused.
        clock.UtcNow += HostedAuth.MailCooldown;
        var challenge = await ChallengeAsync(oidc, user.Email);
        using (var wrong = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = WrongCode(secret, clock) }))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();
        }

        using (var forged = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge + "x", ["code"] = Code(secret, clock) }))
        {
            await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(forged)).IsNull();
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

        await Assert.That((await oidc.OkAsync(HttpMethod.Get, "/v1/terrascale/account/mfa", access)).GetProperty("recovery_codes_remaining").GetInt32()).IsEqualTo(7);

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
    public async Task An_owner_with_a_factor_requires_it_and_a_member_without_one_is_refused_until_enrolled()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (owner, ownerToken) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(ownerToken);
        var policy = $"{Organizations}/{organization}/mfa-policy";
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, policy, ownerToken, new { require_mfa = true })).IsEqualTo((409, "mfa_not_enrolled"));

        clock.UtcNow += HostedAuth.MailCooldown;
        var session = await oidc.SignInAsync(owner.Email);
        var (secret, _, enrolled) = await EnrolAsync(oidc, session, clock);
        var (verifier, pkce) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(enrolled, pkce), verifier);
        ownerToken = (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
        var changed = await oidc.OkAsync(HttpMethod.Put, policy, ownerToken, new { require_mfa = true });
        await Assert.That(changed.GetProperty("require_mfa").GetBoolean()).IsTrue();

        var (member, memberToken) = await oidc.SignInUserAsync("member@example.test");
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().AddMemberAsync(organization, member.Id, Roles.Admin, CancellationToken.None)).IsTrue();
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{organization}", memberToken)).IsEqualTo((403, PlatformDirectory.MfaRequiredCode));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{organization}/api-keys", memberToken, new { name = "backend", scopes = ReadScopes }))
            .IsEqualTo((403, PlatformDirectory.MfaRequiredCode));
        var listed = await oidc.OkAsync(HttpMethod.Get, Organizations, memberToken);
        await Assert.That(listed.GetProperty("items")[0].GetProperty("require_mfa").GetBoolean()).IsTrue();
        using (var exchange = await oidc.ExchangeAsync(memberToken, new() { ["organization"] = organization }))
        {
            await Assert.That(await OidcHarness.ErrorAsync(exchange)).IsEqualTo("access_denied");
        }

        // Leaving and deleting one's own account never need the second factor.
        var (leaver, leaverToken) = await oidc.SignInUserAsync("leaver@example.test");
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().AddMemberAsync(organization, leaver.Id, Roles.Member, CancellationToken.None)).IsTrue();
        await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/leave", leaverToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().AddMemberAsync(organization, leaver.Id, Roles.Member, CancellationToken.None)).IsTrue();
        using (var deleted = await oidc.CallAsync(HttpMethod.Delete, "/v1/terrascale/account", leaverToken))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        // Once the member enrols, the organization opens again; the owner's audit log has both changes.
        clock.UtcNow += HostedAuth.MailCooldown;
        var memberSession = await oidc.SignInAsync(member.Email);
        var (_, _, memberEnrolled) = await EnrolAsync(oidc, memberSession, clock);
        var (memberVerifier, memberPkce) = OidcHarness.Pkce();
        using var memberRedeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(memberEnrolled, memberPkce), memberVerifier);
        memberToken = (await OidcHarness.JsonAsync(memberRedeemed)).GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}", memberToken);
        var audit = await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/audit-events", ownerToken);
        var actions = audit.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("action").GetString()).ToList();
        await Assert.That(actions).Contains("organization.mfa_required");
        await Assert.That(actions).Contains("member.mfa_enabled");
        await Assert.That(secret).IsNotEmpty();
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

    // A code from an hour away is outside the one-step verification window.
    private static string WrongCode(string secret, StoreTestClock clock) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.UtcNow.UtcDateTime.AddHours(1));

    [GeneratedRegex("Enter this key: <code>([A-Z2-7]+)</code>")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("<li><code>([A-Za-z0-9]{24})</code></li>")]
    private static partial Regex RecoveryPattern();

    [GeneratedRegex("name=\"challenge\" value=\"([^\"]+)\"")]
    private static partial Regex ChallengePattern();
}
