using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
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
    private const string ServiceId = "ts-orchestrator";
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

        // A sign-up code redeems only with its pending token; trying without it charges no guess.
        foreach (var token in new string?[] { null, pending + "x" })
        {
            using var refused = await oidc.GrantAsync(HeadlessGrants.EmailCode, "dana@example.test", code, Ip, pendingToken: token);
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
        }

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
        await using var oidc = await StartAsync();
        var owner = await SignUpAsync(oidc, "frank@example.test", "the owner's password");
        var ownerCode = await oidc.Email.NextCodeAsync("frank@example.test");

        // A stranger signs the same address up with their own password: the owner's inbox receives the new code.
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
        await using var oidc = await StartAsync();
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
        await using var oidc = await StartAsync();
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
        await using var oidc = await StartAsync(clock: new ManualClock(DateTimeOffset.UtcNow));
        await CreateUserAsync(oidc, "alice@example.test", Password);

        foreach (var email in new[] { "alice@example.test", "nobody@example.test" })
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
        await using var oidc = await StartAsync(clock: new ManualClock(DateTimeOffset.UtcNow));
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
            await Assert.That(await OidcHarness.ErrorAsync(dead)).IsEqualTo("invalid_grant");
        }

        // The call budget: IPv6 addresses count per /64.
        for (var call = 0; call < HeadlessAuth.PerIp; call++)
        {
            using var counted = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", wrong, $"2001:db8:0:1::{call + 1:x}");
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

        using (var otherPrefix = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", wrong, "2001:db8:0:2::1"))
        {
            await Assert.That(await OidcHarness.ErrorAsync(otherPrefix)).IsEqualTo("invalid_grant");
        }

        // A /48 holds four times one /64's budget, however many /64s it spreads the calls over.
        for (var call = 0; call < HeadlessAuth.PerIp * HeadlessAuth.SiteFactor; call++)
        {
            using var counted = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", wrong, $"2001:db8:5:{call:x}::1");
            await Assert.That(counted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var site = await oidc.GrantAsync(HeadlessGrants.EmailCode, "alice@example.test", wrong, "2001:db8:5:ffff::1"))
        {
            await Assert.That(site.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        // The mail budget, for addresses with and without an account alike.
        for (var mail = 0; mail < HeadlessAuth.MailPerIp; mail++)
        {
            using var sent = await oidc.HeadlessAsync("password-reset/start", new { email = $"user{mail}@example.test", ip_address = "192.0.2.9" });
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
        await using var oidc = await StartAsync();
        await using var replica = await oidc.StartReplicaAsync(values => values["access-token-audiences"] = Audience);
        var pending = await SignUpAsync(oidc, "erin@example.test", Password);
        var code = await oidc.Email.NextCodeAsync("erin@example.test");
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            oidc.GrantAsync(HeadlessGrants.EmailCode, "erin@example.test", code, $"198.51.100.{index}", client: index % 2 == 0 ? oidc.Client : replica.Client, pendingToken: pending)));
        try
        {
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(1);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest)).IsEqualTo(5);
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
