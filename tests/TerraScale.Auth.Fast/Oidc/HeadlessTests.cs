using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Features.SignIn;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Oidc;

/// <summary>The headless API ts-dashboard drives server-to-server (TS-582), over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class HeadlessTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://gru.api.terrascale.tech";
    private const string Ip = "203.0.113.7";
    private const string Password = "correct horse battery";
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";

    [Test]
    public async Task Sign_up_then_the_emailed_code_and_the_password_yield_redirect_flow_tokens_and_revocation_ends_the_sign_in()
    {
        var logs = new CapturingLogs();
        await using var oidc = await StartAsync(logs);
        string pending;
        using (var signUp = await oidc.HeadlessAsync("sign-up", new { email = " Dana@Example.TEST ", password = Password, ip_address = Ip, user_agent = "test", name = "Dana" }))
        {
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(signUp.Headers.CacheControl!.NoStore).IsTrue();
            pending = (await OidcHarness.JsonAsync(signUp)).GetProperty("pending_token").GetString()!;
        }

        var (recipient, mail) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("dana@example.test");
        await Assert.That(mail.Subject).Contains("verification code");
        var code = System.Text.RegularExpressions.Regex.Match(mail.Text, @"\b[0-9]{6}\b").Value;

        // The account exists only once the code is redeemed.
        using (var early = await oidc.GrantAsync(HeadlessGrants.Password, "dana@example.test", Password, Ip))
        {
            await Assert.That(await OidcHarness.ErrorAsync(early)).IsEqualTo("invalid_grant");
        }

        // A sign-up code redeems only with its pending token; missing or wrong tokens leave its local guess counter alone.
        foreach (var token in new string?[] { null, pending + "x" })
        {
            using var refused = await oidc.GrantAsync(HeadlessGrants.EmailCode, "dana@example.test", code, Ip, pendingToken: token);
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
        }

        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Document.GetProperty("failedAttempts").GetInt32()).IsEqualTo(0);

        // A scope a sign-in never grants is refused before the code is spent, so the corrected request below succeeds.
        using (var scoped = await oidc.GrantAsync(HeadlessGrants.EmailCode, "dana@example.test", code, Ip, scope: "openid documents:read", pendingToken: pending))
        {
            await Assert.That(await OidcHarness.ErrorAsync(scoped)).IsEqualTo("invalid_scope");
        }

        using var redeemed = await oidc.GrantAsync(HeadlessGrants.EmailCode, "dana@example.test", code, Ip, pendingToken: pending);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, "dana@example.test"))!;
        var keys = new JsonWebKeySet(await oidc.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json"));
        var access = await ValidateAsync(tokens.GetProperty("access_token").GetString()!, keys, Audience);
        await Assert.That(access.Typ).IsEqualTo("at+jwt");
        await Assert.That(access.Subject).IsEqualTo(user.Id);
        await Assert.That(access.GetClaim("email").Value).IsEqualTo("dana@example.test");
        await Assert.That(access.GetClaim("email_verified").Value).IsEqualTo("true");
        var identity = await ValidateAsync(tokens.GetProperty("id_token").GetString()!, keys, OidcIssuer.DashboardClientId);
        await Assert.That(identity.Subject).IsEqualTo(user.Id);
        await Assert.That(tokens.GetProperty("token_type").GetString()).IsEqualTo("Bearer");

        using (var replay = await oidc.GrantAsync(HeadlessGrants.EmailCode, "dana@example.test", code, Ip, pendingToken: pending))
        {
            await Assert.That(await OidcHarness.ErrorAsync(replay)).IsEqualTo("invalid_grant");
        }

        using var password = await oidc.GrantAsync(HeadlessGrants.Password, "DANA@example.test", Password, Ip);
        await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var second = await OidcHarness.JsonAsync(password);

        using var refreshed = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var rotated = await OidcHarness.JsonAsync(refreshed);

        // Signing out revokes the whole sign-in, the rotated refresh token and the access tokens included...
        using (var revoked = await oidc.RevokeAsync(rotated.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var refused = await oidc.RefreshAsync(rotated.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
        }

        using (var userinfo = await oidc.UserInfoAsync(rotated.GetProperty("access_token").GetString()!))
        {
            await Assert.That(userinfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        // ...and only that one: the password sign-in is untouched.
        using var other = await oidc.RefreshAsync(second.GetProperty("refresh_token").GetString()!);
        await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Logs carry the audit lines but never the address, password, code, pending token or IP.
        await Assert.That(logs.Text).Contains($"Audit: headless sign-up created user {user.Id}");
        foreach (var secret in new[] { "dana@example.test", "Dana@Example", Password, code, pending, Ip })
        {
            await Assert.That(logs.Text).DoesNotContain(secret);
        }
    }

    [Test]
    public async Task A_third_party_cannot_attach_a_password_to_someone_elses_sign_up()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await StartAsync(clock: clock);
        var owner = await SignUpAsync(oidc, "frank@example.test", "the owner's password");
        var ownerCode = await oidc.Email.NextCodeAsync("frank@example.test");

        // A stranger signs the same address up with their own password: the owner's inbox receives the new code.
        clock.Advance(HeadlessAuth.MailCooldown);
        var stranger = await SignUpAsync(oidc, "frank@example.test", "the stranger's password", "198.51.100.66");
        var strangerCode = await oidc.Email.NextCodeAsync("frank@example.test");

        // The owner, redeeming the newest code with their own token, cannot complete the stranger's sign-up; their
        // own code was superseded.
        foreach (var code in new[] { strangerCode, ownerCode })
        {
            using var refused = await oidc.GrantAsync(HeadlessGrants.EmailCode, "frank@example.test", code, Ip, pendingToken: owner);
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
        }

        // The stranger holds the token but not the inbox: a wrong guess fails.
        using (var guess = await oidc.GrantAsync(HeadlessGrants.EmailCode, "frank@example.test", strangerCode == "000000" ? "111111" : "000000", Ip, pendingToken: stranger))
        {
            await Assert.That(await OidcHarness.ErrorAsync(guess)).IsEqualTo("invalid_grant");
        }

        // Signing up again gives the owner a fresh code and token, and the account gets the owner's password only.
        clock.Advance(HeadlessAuth.MailCooldown);
        var again = await SignUpAsync(oidc, "frank@example.test", "the owner's password");
        using (var created = await oidc.GrantAsync(HeadlessGrants.EmailCode, "frank@example.test", await oidc.Email.NextCodeAsync("frank@example.test"), Ip, pendingToken: again))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var strangers = await oidc.GrantAsync(HeadlessGrants.Password, "frank@example.test", "the stranger's password", Ip))
        {
            await Assert.That(await OidcHarness.ErrorAsync(strangers)).IsEqualTo("invalid_grant");
        }

        using var owners = await oidc.GrantAsync(HeadlessGrants.Password, "frank@example.test", "the owner's password", Ip);
        await Assert.That(owners.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Answers_never_tell_an_account_from_a_stranger()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await StartAsync(clock: clock);
        var alice = await CreateUserAsync(oidc, "alice@example.test", Password);
        await oidc.CreateUserAsync("nopassword@example.test");

        // Unknown address, wrong password and an account without a password are one and the same failure.
        var bodies = new List<string>();
        foreach (var (email, password) in new[] { ("nobody@example.test", Password), ("alice@example.test", Password + "x"), ("nopassword@example.test", Password), ("not-an-email", Password) })
        {
            using var refused = await oidc.GrantAsync(HeadlessGrants.Password, email, password, Ip);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            bodies.Add(await refused.Content.ReadAsStringAsync());
        }

        await Assert.That(bodies.Distinct().Count()).IsEqualTo(1);
        await Assert.That(bodies[0]).Contains("invalid_grant");

        // Mail goes only to accounts that exist, though every start answers 202.
        foreach (var (path, body) in new (string, object)[]
        {
            ("email-code/start", new { email = "nobody@example.test", ip_address = Ip }),
            ("password-reset/start", new { email = "nobody@example.test", ip_address = Ip }),
            ("email-code/start", new { email = "alice@example.test", ip_address = Ip }),
        })
        {
            using var accepted = await oidc.HeadlessAsync(path, body);
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await accepted.Content.ReadAsStringAsync()).IsEmpty();
            clock.Advance(HeadlessAuth.MailCooldown);
        }

        await Assert.That((await oidc.Email.NextAsync()).Recipient).IsEqualTo("alice@example.test");
        await Assert.That(oidc.Email.HasPending).IsFalse();

        // Signing up with a taken address mails that account a sign-in code and keeps its password.
        using (var signUp = await oidc.HeadlessAsync("sign-up", new { email = "alice@example.test", password = "another password", ip_address = Ip }))
        {
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That((await OidcHarness.JsonAsync(signUp)).GetProperty("pending_token").GetString()!.Length).IsEqualTo(43);
        }

        var (recipient, mail) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("alice@example.test");
        await Assert.That(mail.Subject).Contains("sign-in code");
        using var signedIn = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", System.Text.RegularExpressions.Regex.Match(mail.Text, @"\b[0-9]{6}\b").Value, Ip);
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(new JsonWebToken((await OidcHarness.JsonAsync(signedIn)).GetProperty("id_token").GetString()!).Subject).IsEqualTo(alice.Id);
        using (var kept = await oidc.GrantAsync(HeadlessGrants.Password, "alice@example.test", Password, Ip))
        {
            await Assert.That(kept.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var discarded = await oidc.GrantAsync(HeadlessGrants.Password, "alice@example.test", "another password", Ip))
        {
            await Assert.That(await OidcHarness.ErrorAsync(discarded)).IsEqualTo("invalid_grant");
        }

        // A reset for an unknown address fails like a wrong code.
        using var unknownReset = await oidc.HeadlessAsync("password-reset/complete", new { email = "nobody@example.test", code = "123456", new_password = "brand new password", ip_address = Ip });
        await Assert.That(unknownReset.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(unknownReset)).IsEqualTo("invalid_code");
    }

    [Test]
    public async Task A_password_reset_code_works_once_for_its_purpose_and_signs_the_user_out_everywhere()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await StartAsync(clock: clock);
        await CreateUserAsync(oidc, "alice@example.test", Password);
        using var before = await oidc.GrantAsync(HeadlessGrants.Password, "alice@example.test", Password, Ip);
        var earlier = (await OidcHarness.JsonAsync(before)).GetProperty("refresh_token").GetString()!;

        using (var start = await oidc.HeadlessAsync("password-reset/start", new { email = "alice@example.test", ip_address = Ip }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        var (_, mail) = await oidc.Email.NextAsync();
        await Assert.That(mail.Subject).Contains("password reset code");
        var code = System.Text.RegularExpressions.Regex.Match(mail.Text, @"\b[0-9]{6}\b").Value;

        // A reset code never signs in, and a password outside the policy keeps the code alive.
        using (var notSignIn = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", code, Ip))
        {
            await Assert.That(await OidcHarness.ErrorAsync(notSignIn)).IsEqualTo("invalid_grant");
        }

        foreach (var weak in new[] { "short", new string('x', PasswordHasher.MaxBytes + 1) })
        {
            using var refused = await oidc.HeadlessAsync("password-reset/complete", new { email = "alice@example.test", code, new_password = weak, ip_address = Ip });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("invalid_password");
        }

        using (var reset = await oidc.HeadlessAsync("password-reset/complete", new { email = "alice@example.test", code, new_password = "brand new password", ip_address = Ip }))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        using (var again = await oidc.HeadlessAsync("password-reset/complete", new { email = "alice@example.test", code, new_password = "a third password", ip_address = Ip }))
        {
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(again)).IsEqualTo("invalid_code");
        }

        using (var old = await oidc.GrantAsync(HeadlessGrants.Password, "alice@example.test", Password, Ip))
        {
            await Assert.That(await OidcHarness.ErrorAsync(old)).IsEqualTo("invalid_grant");
        }

        using (var current = await oidc.GrantAsync(HeadlessGrants.Password, "alice@example.test", "brand new password", Ip))
        {
            await Assert.That(current.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var stale = await oidc.RefreshAsync(earlier);
        await Assert.That(await OidcHarness.ErrorAsync(stale)).IsEqualTo("invalid_grant");

        // A sign-in code cannot reset the password either.
        clock.Advance(HeadlessAuth.MailCooldown);
        using (var signInStart = await oidc.HeadlessAsync("email-code/start", new { email = "alice@example.test", ip_address = Ip }))
        {
            await Assert.That(signInStart.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        var signInCode = await oidc.Email.NextCodeAsync("alice@example.test");
        using var wrongPurpose = await oidc.HeadlessAsync("password-reset/complete", new { email = "alice@example.test", code = signInCode, new_password = "a third password", ip_address = Ip });
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(wrongPurpose)).IsEqualTo("invalid_code");
    }

    [Test]
    public async Task Password_guesses_are_capped_per_email_alike_for_strangers_and_an_email_code_still_works()
    {
        // A frozen clock keeps every attempt in one limiter window.
        await using var oidc = await StartAsync(clock: new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600)));
        await CreateUserAsync(oidc, "alice@example.test", Password);
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var unknown = Enumerable.Range(0, 128).Select(index => $"nobody{index}@example.test")
            .First(email => limiter.Fold(email) != limiter.Fold("alice@example.test"));

        foreach (var email in new[] { "alice@example.test", unknown })
        {
            for (var attempt = 0; attempt < HeadlessAuth.PasswordPerEmail; attempt++)
            {
                // A fresh IP each time: the per-email budget alone stops this.
                using var wrong = await oidc.GrantAsync(HeadlessGrants.Password, email, "wrong password", $"198.51.100.{attempt}");
                await Assert.That(await OidcHarness.ErrorAsync(wrong)).IsEqualTo("invalid_grant");
            }

            using var locked = await oidc.GrantAsync(HeadlessGrants.Password, email, Password, "198.51.100.200");
            await Assert.That(locked.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.ErrorAsync(locked)).IsEqualTo("rate_limited");
            await Assert.That(locked.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.PasswordWindow);
        }

        var (recipient, notice) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("alice@example.test");
        await Assert.That(notice.Subject).Contains("Temporary authentication pause");

        using (var start = await oidc.HeadlessAsync("email-code/start", new { email = "alice@example.test", ip_address = Ip }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        var code = await oidc.Email.NextCodeAsync("alice@example.test");
        using var signedIn = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", code, Ip);
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_code_dies_after_five_wrong_guesses_and_each_ip_has_a_call_and_a_mail_budget()
    {
        // A frozen clock keeps every call in one limiter window.
        await using var oidc = await StartAsync(clock: new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600)));
        await CreateUserAsync(oidc, "alice@example.test", Password);
        using (var start = await oidc.HeadlessAsync("email-code/start", new { email = "alice@example.test", ip_address = Ip }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        var code = await oidc.Email.NextCodeAsync("alice@example.test");
        var wrong = code == "000000" ? "111111" : "000000";
        for (var guess = 0; guess < Passcodes.MaxAttempts; guess++)
        {
            using var refused = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", wrong, "198.51.100.1");
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
        }

        using (var dead = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", code, "198.51.100.1"))
        {
            await Assert.That(dead.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.ErrorAsync(dead)).IsEqualTo("rate_limited");
            await Assert.That(dead.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.CodeWindow);
        }

        var (recipient, notice) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("alice@example.test");
        await Assert.That(notice.Subject).Contains("Temporary authentication pause");

        // Select separate email buckets so this IP test cannot reuse Alice's exhausted budget.
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var exhausted = limiter.Fold("alice@example.test");
        var emailCount = HeadlessAuth.PerIp * (HeadlessAuth.SiteFactor + 1) + HeadlessAuth.MailPerIp + 1;
        var emails = Enumerable.Range(0, emailCount * 2).Select(index => $"ip-call{index}@example.test")
            .Where(email => limiter.Fold(email) != exhausted).DistinctBy(limiter.Fold).Take(emailCount).ToArray();
        await Assert.That(emails.Length).IsEqualTo(emailCount);

        // The call budget: IPv6 addresses count per /64.
        for (var call = 0; call < HeadlessAuth.PerIp; call++)
        {
            using var counted = await oidc.GrantAsync(HeadlessGrants.EmailCode, emails[call], wrong, $"2001:db8:0:1::{call + 1:x}");
            await Assert.That(counted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var limited = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", wrong, "2001:db8:0:1:ffff::1"))
        {
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(limited.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromMinutes(1));
        }

        using (var json = await oidc.HeadlessAsync("email-code/start", new { email = "alice@example.test", ip_address = "2001:db8:0:1::abcd" }))
        {
            await Assert.That(json.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(json)).IsEqualTo("rate_limited");
        }

        using (var otherPrefix = await oidc.GrantAsync(HeadlessGrants.EmailCode, emails[HeadlessAuth.PerIp], wrong, "2001:db8:0:2::1"))
        {
            await Assert.That(await OidcHarness.ErrorAsync(otherPrefix)).IsEqualTo("invalid_grant");
        }

        // A /48 holds four times one /64's budget, however many /64s it spreads the calls over.
        for (var call = 0; call < HeadlessAuth.PerIp * HeadlessAuth.SiteFactor; call++)
        {
            using var counted = await oidc.GrantAsync(HeadlessGrants.EmailCode, emails[HeadlessAuth.PerIp + 1 + call], wrong, $"2001:db8:5:{call:x}::1");
            await Assert.That(counted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var site = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", wrong, "2001:db8:5:ffff::1"))
        {
            await Assert.That(site.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        // The mail budget, for addresses with and without an account alike.
        for (var mail = 0; mail < HeadlessAuth.MailPerIp; mail++)
        {
            using var sent = await oidc.HeadlessAsync("password-reset/start", new { email = emails[emailCount - HeadlessAuth.MailPerIp + mail], ip_address = "192.0.2.9" });
            await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        using var mailLimited = await oidc.HeadlessAsync("sign-up", new { email = "new@example.test", password = Password, ip_address = "192.0.2.9" });
        await Assert.That(mailLimited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(mailLimited.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.MailWindow);
    }

    [Test]
    public async Task Only_the_dashboard_client_with_well_formed_requests_reaches_the_headless_api()
    {
        await using var oidc = await StartAsync();
        var body = new { email = "alice@example.test", ip_address = Ip };

        foreach (var (clientId, secret) in new[] { (OidcIssuer.DashboardClientId, (string?)null), (OidcIssuer.DashboardClientId, OidcHarness.ClientSecret + "x"), (ServiceId, ServiceSecret), ("unknown", ServiceSecret) })
        {
            using var refused = await oidc.HeadlessAsync("email-code/start", body, clientId, secret);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("invalid_client");
            await Assert.That(refused.Headers.WwwAuthenticate.Single().Scheme).IsEqualTo("Basic");
        }

        using (var otherSlug = await oidc.HeadlessAsync("email-code/start", body, slug: "acme"))
        {
            await Assert.That(otherSlug.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // A service client may not use the headless grants.
        using (var service = await TokenAsServiceAsync(oidc))
        {
            await Assert.That(await OidcHarness.ErrorAsync(service)).IsEqualTo("unauthorized_client");
        }

        foreach (var invalid in new object[]
        {
            new { email = "alice@example.test" },
            new { email = "alice@example.test", ip_address = "not-an-ip" },
            new { email = "not-an-email", ip_address = Ip },
        })
        {
            using var refused = await oidc.HeadlessAsync("email-code/start", invalid);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("invalid_request");
        }

        using (var weak = await oidc.HeadlessAsync("sign-up", new { email = "new@example.test", password = "short", ip_address = Ip }))
        {
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(weak)).IsEqualTo("invalid_password");
        }

        using (var notJson = await oidc.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/headless/v1/sign-up")
        {
            Content = new StringContent("{\"email\":", System.Text.Encoding.UTF8, "application/json"),
            Headers = { Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}"))) },
        }))
        {
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(notJson)).IsEqualTo("invalid_request");
        }

        using var noIp = await oidc.GrantAsync(HeadlessGrants.Password, "alice@example.test", Password, ipAddress: null);
        await Assert.That(noIp.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(noIp)).IsEqualTo("invalid_request");
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Without_its_limiter_storage_the_api_fails_closed_with_a_retryable_error_in_each_format()
    {
        await using var oidc = await StartAsync();
        await CreateUserAsync(oidc, "alice@example.test", Password);
        await oidc.Database.DropCollectionAsync("rate_limits");

        // The token endpoint answers an OAuth error, never the JSON API envelope or a sign-in.
        using var grant = await oidc.GrantAsync(HeadlessGrants.Password, "alice@example.test", Password, Ip);
        await Assert.That(grant.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await OidcHarness.ErrorAsync(grant)).IsEqualTo("temporarily_unavailable");

        using var start = await oidc.HeadlessAsync("email-code/start", new { email = "alice@example.test", ip_address = Ip });
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(start)).IsEqualTo("temporarily_unavailable");
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Concurrent_redemptions_of_one_sign_up_code_across_replicas_create_one_account()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await StartAsync(clock: clock);
        await using var replica = await oidc.StartReplicaAsync(values => values["access-token-audiences"] = Audience, clock: clock);
        var pending = await SignUpAsync(oidc, "erin@example.test", Password);
        var code = await oidc.Email.NextCodeAsync("erin@example.test");
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            oidc.GrantAsync(HeadlessGrants.EmailCode, "erin@example.test", code, $"198.51.100.{index}", client: index % 2 == 0 ? oidc.Client : replica.Client, pendingToken: pending)));
        try
        {
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(1);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest)).IsEqualTo(4);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests)).IsEqualTo(1);
            await Assert.That(await OidcHarness.ErrorAsync(responses.Single(response => response.StatusCode == HttpStatusCode.TooManyRequests))).IsEqualTo("rate_limited");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var accounts = (await oidc.Database.ListDocumentsAsync(AuthStore.UserCollection)).Items;
        await Assert.That(accounts.Count).IsEqualTo(1);
        await Assert.That(accounts[0].Document.GetRawText()).DoesNotContain(Password);
        var consumed = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Document;
        await Assert.That(consumed.TryGetProperty("passwordHash", out var hash) && hash.ValueKind != JsonValueKind.Null).IsFalse();
    }

    [Test]
    public async Task Resending_a_pending_sign_up_preserves_its_password_and_token_and_invalidates_the_old_code()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600));
        await using var oidc = await StartAsync(clock: clock);
        const string email = "resend@example.test";
        var pending = await SignUpAsync(oidc, email, Password);
        var oldCode = await oidc.Email.NextCodeAsync(email);
        var original = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Document;
        clock.Advance(HeadlessAuth.MailCooldown);

        using (var resend = await oidc.HeadlessAsync("email-code/start", new { email = " RESEND@Example.TEST ", ip_address = Ip, pending_token = pending }))
        {
            await Assert.That(resend.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await resend.Content.ReadAsStringAsync()).IsEmpty();
        }

        var code = await oidc.Email.NextCodeAsync(email);
        await Assert.That(code).IsNotEqualTo(oldCode);
        var current = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Document;
        foreach (var field in new[] { "passwordHash", "pendingTokenHash", "userId", "windowStartedAt" })
        {
            await Assert.That(current.GetProperty(field).GetRawText()).IsEqualTo(original.GetProperty(field).GetRawText());
        }

        await Assert.That(current.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(2);
        using (var old = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, oldCode, Ip, pendingToken: pending))
        {
            await Assert.That(await OidcHarness.ErrorAsync(old)).IsEqualTo("invalid_grant");
        }

        using (var wrongToken = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, code, Ip, pendingToken: "another-requester's-token"))
        {
            await Assert.That(await OidcHarness.ErrorAsync(wrongToken)).IsEqualTo("invalid_grant");
        }

        using (var created = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, code, Ip, pendingToken: pending))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var password = await oidc.GrantAsync(HeadlessGrants.Password, email, Password, Ip);
        await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var consumed = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single();
        clock.Advance(HeadlessAuth.MailCooldown);
        using var existing = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = Ip, pending_token = "wrong-token" });
        await Assert.That(existing.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        using var signedIn = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, await oidc.Email.NextCodeAsync(email), Ip);
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var signup = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single(item => item.Id == consumed.Id);
        await Assert.That(signup.Version).IsEqualTo(consumed.Version);
    }

    [Test]
    public async Task A_missing_or_wrong_resend_token_changes_nothing_and_resends_keep_the_hourly_budget()
    {
        var windowStart = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600);
        var clock = new ManualClock(windowStart.AddMinutes(50));
        await using var oidc = await StartAsync(clock: clock);
        const string email = "budget@example.test";
        var pending = await SignUpAsync(oidc, email, Password);
        await oidc.Email.NextCodeAsync(email);
        var passcodes = oidc.Server.App.Services.GetRequiredService<Passcodes>();
        var before = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single();
        foreach (var token in new string?[] { null, "wrong-token" })
        {
            clock.Advance(HeadlessAuth.MailCooldown);
            using var response = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = Ip, pending_token = token });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEmpty();
            // A marker behind the request on the same worker proves it issued and delivered no code.
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            oidc.Server.App.Services.GetRequiredService<SignInMail>().EnqueueIssue(() =>
            {
                drained.SetResult();
                return Task.FromResult<IssuedPasscode?>(null);
            });
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(oidc.Email.HasPending).IsFalse();
        }

        foreach (var token in new[] { "wrong-token", string.Empty })
        {
            await Assert.That(await passcodes.ResendSignUpAsync(OidcIssuer.InstanceId, email, token)).IsNull();
        }

        await Assert.That(await passcodes.RequestAsync(OidcIssuer.InstanceId, email)).IsNull();
        await Assert.That(await passcodes.ResendSignUpAsync(OidcIssuer.InstanceId, "unknown@example.test", pending)).IsNull();
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Version).IsEqualTo(before.Version);

        // Invalid resend requests also spend the shared HTTP budget. Issue five real resends at minutes 50 through 54.
        var nextHour = windowStart + Passcodes.IssueWindow;
        clock.Advance(nextHour.AddMinutes(50) - clock.GetUtcNow());
        for (var issued = 0; issued < Passcodes.MaxIssuesPerWindow; issued++)
        {
            if (issued > 0)
            {
                clock.Advance(HeadlessAuth.MailCooldown);
            }

            using var resend = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = Ip, pending_token = pending });
            await Assert.That(resend.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await oidc.Email.NextCodeAsync(email);
        }

        var last = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single();
        await Assert.That(last.Document.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(nextHour);
        await Assert.That(last.Document.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(Passcodes.MaxIssuesPerWindow);
        clock.Advance(HeadlessAuth.MailCooldown);
        var remaining = nextHour + Passcodes.IssueWindow - clock.GetUtcNow();
        using (var limited = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = Ip, pending_token = pending }))
        {
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(limited)).IsEqualTo("rate_limited");
            await Assert.That(limited.Headers.RetryAfter!.Delta).IsEqualTo(remaining);
        }

        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That(await passcodes.ResendSignUpAsync(OidcIssuer.InstanceId, email, pending)).IsNull();
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Version).IsEqualTo(last.Version);
        clock.Advance(remaining);
        using (var resend = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = Ip, pending_token = pending }))
        {
            await Assert.That(resend.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await resend.Content.ReadAsStringAsync()).IsEmpty();
        }

        var code = await oidc.Email.NextCodeAsync(email);
        var renewed = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Document;
        await Assert.That(renewed.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(clock.GetUtcNow());
        await Assert.That(renewed.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(1);
        foreach (var field in new[] { "passwordHash", "pendingTokenHash", "userId" })
        {
            await Assert.That(renewed.GetProperty(field).GetRawText()).IsEqualTo(before.Document.GetProperty(field).GetRawText());
        }

        using var created = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, code, Ip, pendingToken: pending);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var password = await oidc.GrantAsync(HeadlessGrants.Password, email, Password, Ip);
        await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Sign_in_and_reset_mail_budgets_renew_at_the_next_UTC_hour_after_a_late_first_issue(bool reset, bool nearHourEnd)
    {
        var windowStart = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600);
        var clock = new ManualClock(windowStart.AddMinutes(50));
        await using var oidc = await StartAsync(clock: clock);
        const string email = "hourly@example.test";
        await CreateUserAsync(oidc, email, Password);
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var unknown = Enumerable.Range(0, 128).Select(index => $"unknown{index}@example.test")
            .First(address => limiter.Fold(address) != limiter.Fold(email));
        var path = reset ? "password-reset/start" : "email-code/start";
        for (var issued = 0; issued < Passcodes.MaxIssuesPerWindow; issued++)
        {
            if (issued > 0)
            {
                clock.Advance(HeadlessAuth.MailCooldown);
            }

            // Queue the unknown address first; delivery of the known code also proves the worker drained it.
            using var stranger = await oidc.HeadlessAsync(path, new { email = unknown, ip_address = $"198.51.100.{issued}" });
            using var known = await oidc.HeadlessAsync(path, new { email, ip_address = $"203.0.113.{issued + 10}" });
            await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(known.StatusCode).IsEqualTo(stranger.StatusCode);
            await Assert.That(await known.Content.ReadAsStringAsync()).IsEqualTo(await stranger.Content.ReadAsStringAsync());
            await oidc.Email.NextCodeAsync(email);
            await Assert.That(oidc.Email.HasPending).IsFalse();
        }

        var full = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single();
        await Assert.That(full.Document.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(windowStart);
        await Assert.That(full.Document.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(Passcodes.MaxIssuesPerWindow);
        var nextHour = windowStart + Passcodes.IssueWindow;
        clock.Advance(nearHourEnd ? nextHour.AddSeconds(-10) - clock.GetUtcNow() : HeadlessAuth.MailCooldown);
        var remaining = nearHourEnd ? HeadlessAuth.MailCooldown : nextHour - clock.GetUtcNow();
        DateTimeOffset retryAt;
        using (var stranger = await oidc.HeadlessAsync(path, new { email = unknown, ip_address = "198.51.100.20" }))
        using (var known = await oidc.HeadlessAsync(path, new { email, ip_address = Ip }))
        {
            await Assert.That(known.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(stranger.StatusCode).IsEqualTo(known.StatusCode);
            await Assert.That(known.Headers.RetryAfter!.Delta).IsEqualTo(remaining);
            await Assert.That(stranger.Headers.RetryAfter!.Delta).IsEqualTo(remaining);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(known)).IsEqualTo("rate_limited");
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(stranger)).IsEqualTo("rate_limited");
            retryAt = clock.GetUtcNow() + known.Headers.RetryAfter!.Delta!.Value;
        }

        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Version).IsEqualTo(full.Version);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        if (nearHourEnd)
        {
            // The other route/replica must neither extend the admitted cooldowns nor charge hourly budgets on refusal.
            var hourlyWindow = windowStart.ToUnixTimeSeconds() / (long)Passcodes.IssueWindow.TotalSeconds;
            var emailBuckets = (await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items
                .Where(item => item.Document.GetProperty("window").GetInt64() == clock.GetUtcNow().ToUnixTimeMilliseconds()
                    || item.Document.GetProperty("window").GetInt64() == hourlyWindow).ToArray();
            await Assert.That(emailBuckets.Length).IsEqualTo(4);
            await using var replica = await oidc.StartReplicaAsync(clock: clock);
            clock.Advance(nextHour - clock.GetUtcNow());
            foreach (var address in new[] { unknown, email })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/headless/v1/sign-up")
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new { email = $" {address.ToUpperInvariant()} ", password = Password, ip_address = "192.0.2.22" }),
                };
                request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
                using var limited = await replica.Client.SendAsync(request);
                await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(limited.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(50));
                await Assert.That(await OidcHarness.EnvelopeCodeAsync(limited)).IsEqualTo("rate_limited");
            }

            var afterBoundary = (await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items;
            foreach (var bucket in emailBuckets)
            {
                await Assert.That(afterBoundary.Single(item => item.Id == bucket.Id).Version).IsEqualTo(bucket.Version);
            }

            await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Version).IsEqualTo(full.Version);
            await Assert.That(oidc.Email.HasPending).IsFalse();
        }

        clock.Advance(retryAt - clock.GetUtcNow());
        using (var stranger = await oidc.HeadlessAsync(path, new { email = unknown, ip_address = "198.51.100.20" }))
        using (var renewed = await oidc.HeadlessAsync(path, new { email, ip_address = Ip }))
        {
            await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(stranger.StatusCode).IsEqualTo(renewed.StatusCode);
            await Assert.That(renewed.Headers.RetryAfter).IsNull();
            await Assert.That(stranger.Headers.RetryAfter).IsNull();
            await Assert.That(await renewed.Content.ReadAsStringAsync()).IsEqualTo(await stranger.Content.ReadAsStringAsync());
        }

        var code = await oidc.Email.NextCodeAsync(email);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        var current = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Document;
        await Assert.That(current.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(nextHour);
        await Assert.That(current.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(1);
        using var redeemed = reset
            ? await oidc.HeadlessAsync("password-reset/complete", new { email, code, new_password = "new hourly password", ip_address = Ip })
            : await oidc.GrantAsync(HeadlessGrants.EmailCode, email, code, Ip);
        await Assert.That(redeemed.StatusCode).IsEqualTo(reset ? HttpStatusCode.NoContent : HttpStatusCode.OK);
    }

    [Test]
    public async Task Mail_cooldown_is_shared_across_routes_clients_and_unknown_addresses_for_sixty_elapsed_seconds()
    {
        // Start one millisecond before a fixed minute boundary: crossing it must not reset the cooldown.
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60000 * 60000 + 59999));
        await using var oidc = await StartAsync(clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        await oidc.CreateUserAsync("known@example.test");
        foreach (var email in new[] { "known@example.test", "unknown@example.test" })
        {
            using (var first = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = Ip }))
            {
                await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            }

            foreach (var path in new[] { "email-code/start", "sign-up", "password-reset/start" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/headless/v1/{path}")
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new { email = $" {email.ToUpperInvariant()} ", password = Password, ip_address = "198.51.100.22" }),
                };
                request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
                using var repeated = await replica.Client.SendAsync(request);
                await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(repeated.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.MailCooldown);
                await Assert.That(await OidcHarness.EnvelopeCodeAsync(repeated)).IsEqualTo("rate_limited");
            }

            clock.Advance(TimeSpan.FromSeconds(30));
            using (var midpoint = await oidc.HeadlessAsync("password-reset/start", new { email, ip_address = "192.0.2.21" }))
            {
                await Assert.That(midpoint.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(midpoint.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(30));
            }

            clock.Advance(HeadlessAuth.MailCooldown - TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
            using (var tooSoon = await oidc.HeadlessAsync("password-reset/start", new { email, ip_address = "192.0.2.21" }))
            {
                await Assert.That(tooSoon.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(tooSoon.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            }

            clock.Advance(TimeSpan.FromMilliseconds(1));
            using var allowed = await oidc.HeadlessAsync("sign-up", new { email, password = Password, ip_address = "192.0.2.22" });
            await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }
    }

    [Test]
    public async Task Concurrent_mail_starts_on_two_replicas_admit_one_request_across_routes()
    {
        await using var oidc = await StartAsync();
        await using var replica = await oidc.StartReplicaAsync();
        string[] routes = ["email-code/start", "sign-up", "password-reset/start"];
        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/headless/v1/{routes[index % routes.Length]}")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { email = "concurrent@example.test", password = Password, ip_address = $"198.51.100.{index}" }),
            };
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
            using var response = await (index % 2 == 0 ? oidc.Client : replica.Client).SendAsync(request);
            return response.StatusCode;
        }));
        await Assert.That(statuses.Count(status => status == HttpStatusCode.Accepted)).IsEqualTo(1);
        await Assert.That(statuses.Count(status => status == HttpStatusCode.TooManyRequests)).IsEqualTo(5);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_resend_paused_before_its_CAS_cannot_revive_a_consumed_signup_or_overwrite_a_new_signup(bool replace)
    {
        var pause = new PausingTransactions(request => request.Method == HttpMethod.Put
            && request.RequestUri!.AbsolutePath.Contains("/collections/passcodes/documents/", StringComparison.Ordinal))
        { InnerHandler = fixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(pause)));
        await using var replica = await oidc.StartReplicaAsync();
        const string email = "race@example.test";
        var pending = await SignUpAsync(oidc, email, Password);
        var code = await oidc.Email.NextCodeAsync(email);
        var held = await pause.HoldNextAsync(() => oidc.Server.App.Services.GetRequiredService<Passcodes>().ResendSignUpAsync(OidcIssuer.InstanceId, email, pending));
        try
        {
            var other = replica.App.Services.GetRequiredService<Passcodes>();
            if (replace)
            {
                await Assert.That(await other.RequestSignUpAsync(OidcIssuer.InstanceId, email, Passwords.Hash("replacement password"), "replacement-token")).IsNotNull();
            }
            else
            {
                await Assert.That(await other.RedeemSignUpAsync(OidcIssuer.InstanceId, email, code, pending)).IsNotNull();
            }

            var winner = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single();
            await Assert.That(await pause.ReleaseAsync(held)).IsNull();
            await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Version).IsEqualTo(winner.Version);
            if (replace)
            {
                await Assert.That(await other.ResendSignUpAsync(OidcIssuer.InstanceId, email, pending)).IsNull();
            }
            else
            {
                await Assert.That(winner.Document.GetProperty("consumed").GetBoolean()).IsTrue();
                await Assert.That(winner.Document.GetProperty("passwordHash").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
        }
        finally
        {
            await pause.ReleaseAsync(held);
        }
    }

    private Task<OidcHarness> StartAsync(CapturingLogs? logs = null, TimeProvider? clock = null) => OidcHarness.StartAsync(fixture, values =>
    {
        values["access-token-audiences"] = Audience;
        values["service-clients"] = $"{ServiceId}:{ServiceSecret}";
    }, logs: logs, clock: clock);

    // Starts a sign-up and returns its pending token.
    private static async Task<string> SignUpAsync(OidcHarness oidc, string email, string password, string ip = Ip)
    {
        using var response = await oidc.HeadlessAsync("sign-up", new { email, password, ip_address = ip });
        return response.StatusCode == HttpStatusCode.Accepted
            ? (await OidcHarness.JsonAsync(response)).GetProperty("pending_token").GetString()!
            : throw new InvalidOperationException($"Sign-up answered {response.StatusCode}.");
    }

    private static async Task<UserRecord> CreateUserAsync(OidcHarness oidc, string email, string password) =>
        (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(OidcIssuer.InstanceId, email, Passwords.Hash(password), CancellationToken.None))!;

    private static Task<HttpResponseMessage> TokenAsServiceAsync(OidcHarness oidc)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = HeadlessGrants.Password,
                ["email"] = "alice@example.test",
                ["password"] = Password,
                ["ip_address"] = Ip,
            }),
        };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{ServiceId}:{ServiceSecret}")));
        return oidc.Client.SendAsync(request);
    }

    // Validated as TerraBase does: ES256 only, exact issuer and audience, published keys.
    private static async Task<JsonWebToken> ValidateAsync(string token, JsonWebKeySet keys, string audience)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudience = audience,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = keys.GetSigningKeys(),
            ClockSkew = TimeSpan.Zero,
        });
        return result.IsValid ? (JsonWebToken)result.SecurityToken : throw new InvalidOperationException($"Token rejected: {result.Exception?.GetType().Name}");
    }
}
