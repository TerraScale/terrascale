using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Microsoft.Playwright;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class HostedSignInTests(TerraBaseFixture fixture)
{
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";

    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    public async Task Cross_origin_posts_and_unknown_addresses_send_no_mail()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var form = new Dictionary<string, string> { ["email"] = "alice@example.test", ["next"] = string.Empty };

        foreach (var origin in new[] { null, "https://evil.example" })
        {
            using var refused = await oidc.PostFormAsync(SignIn, form, origin);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            using var refusedVerify = await oidc.PostFormAsync($"{SignIn}/verify", new(form) { ["code"] = "123456" }, origin);
            await Assert.That(refusedVerify.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(OidcHarness.SessionCookie(refusedVerify)).IsNull();
        }

        using var known = await oidc.PostFormAsync(SignIn, form);
        using var unknown = await oidc.PostFormAsync(SignIn, new() { ["email"] = "nobody@example.test", ["next"] = string.Empty });
        await Assert.That(unknown.StatusCode).IsEqualTo(known.StatusCode);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await unknown.Content.ReadAsStringAsync()).Contains("If an account exists for <strong>nobody@example.test</strong>");

        // Exactly one mail went out: the one for the known address from the same-origin post.
        var (recipient, _) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("alice@example.test");
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task A_wrong_code_is_refused_and_the_right_one_sets_a_locked_down_session_cookie()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        using var requested = await oidc.PostFormAsync(SignIn, new() { ["email"] = "alice@example.test", ["next"] = "https://evil.example/" });
        await Assert.That(await requested.Content.ReadAsStringAsync()).DoesNotContain("evil.example");
        var code = await oidc.Email.NextCodeAsync("alice@example.test");

        using var wrong = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = code == "000000" ? "111111" : "000000", ["next"] = string.Empty });
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();

        using var verified = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = code, ["next"] = "//evil.example/terrascale/oauth/authorize?x=1" });
        var page = await verified.Content.ReadAsStringAsync();
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page).Contains("You are signed in.");
        await Assert.That(page).DoesNotContain("evil.example");

        var cookie = SetCookieHeaderValue.ParseList([.. verified.Headers.GetValues("Set-Cookie")]).Single(value => value.Name == "tsauth_session");
        await Assert.That(cookie.Secure).IsTrue();
        await Assert.That(cookie.HttpOnly).IsTrue();
        await Assert.That(cookie.SameSite).IsEqualTo(SameSiteMode.Lax);
        await Assert.That(cookie.Path.ToString()).IsEqualTo("/");
        await Assert.That(cookie.Value.ToString()).IsEqualTo(OidcHarness.SessionCookie(verified)!);
    }

    [Test]
    public async Task The_origin_check_compares_scheme_ascii_host_and_effective_port()
    {
        // The configured origin has an explicit default port, upper case and a Unicode host; a browser sends the
        // serialized ASCII form without the default port.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["public-origin"] = "https://Bücher.Test.Invalid:443", clock: clock);
        await oidc.CreateUserAsync();
        var form = new Dictionary<string, string> { ["email"] = "alice@example.test", ["next"] = string.Empty };

        foreach (var origin in new[]
        {
            "https://xn--bcher-kva.test.invalid:8443", "http://xn--bcher-kva.test.invalid", "https://xn--bcher-kva.test.invalid/",
            "https://user@xn--bcher-kva.test.invalid", "https://auth.test.invalid", "null",
        })
        {
            using var refused = await oidc.PostFormAsync(SignIn, form, origin);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        await Assert.That(oidc.Email.HasPending).IsFalse();
        foreach (var origin in new[] { "https://xn--bcher-kva.test.invalid", "https://XN--BCHER-KVA.test.invalid:443" })
        {
            using var accepted = await oidc.PostFormAsync(SignIn, form, origin);
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await oidc.Email.NextAsync()).Recipient).IsEqualTo("alice@example.test");
            clock.Advance(HostedAuth.MailCooldown);
        }
    }

    [Test]
    public async Task Codes_go_out_after_the_response_one_at_a_time_and_queued_ones_are_sent_when_the_host_stops()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var email = new CapturingEmailSender { Gate = gate };
        await using var oidc = await OidcHarness.StartAsync(fixture, email: email);
        await oidc.CreateUserAsync("alice@example.test");
        await oidc.CreateUserAsync("bob@example.test");

        // Both responses arrive while delivery is blocked: no request waits for its mail.
        foreach (var address in new[] { "alice@example.test", "bob@example.test" })
        {
            using var response = await oidc.PostFormAsync(SignIn, new() { ["email"] = address, ["next"] = string.Empty });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // One worker: the second code waits for the first send instead of opening another connection.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (email.InFlight == 0)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        await Task.Delay(200);
        await Assert.That(email.MaxInFlight).IsEqualTo(1);

        // Stopping waits for the queued code instead of dropping it.
        var stopping = oidc.Server.App.Services.GetRequiredService<SignInMail>().StopAsync(CancellationToken.None);
        await Assert.That(stopping.IsCompleted).IsFalse();
        gate.SetResult();
        await stopping;
        string[] delivered = [(await email.NextAsync()).Recipient, (await email.NextAsync()).Recipient];
        await Assert.That(delivered).IsEquivalentTo(["alice@example.test", "bob@example.test"]);
        await Assert.That(email.MaxInFlight).IsEqualTo(1);
    }

    [Test]
    public async Task Hosted_sign_up_creates_the_account_and_password_sign_in_and_reset_work()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600));
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Address = "new@example.test";
        const string Password = "correct horse battery";

        using var forged = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = Address, ["password"] = Password }, "https://evil.example");
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // Short, common and own-email passwords are refused before any code is mailed.
        foreach (var weak in new[] { "fourteen chars", "1234567890123456", Address })
        {
            using var refused = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = Address, ["password"] = weak, ["next"] = string.Empty });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Choose another password");
        }

        using var started = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = Address, ["password"] = Password, ["next"] = string.Empty });
        var verifyPage = await started.Content.ReadAsStringAsync();
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(verifyPage).Contains("<h1>Verify your email</h1>");
        var pending = PendingToken().Match(verifyPage).Groups[1].Value;
        await Assert.That(pending).IsNotEmpty();
        var code = await oidc.Email.NextCodeAsync(Address);

        var verify = new Dictionary<string, string> { ["email"] = Address, ["code"] = code == "000000" ? "111111" : "000000", ["pending_token"] = pending, ["next"] = string.Empty };
        using (var wrong = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up/verify", verify))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();
            await Assert.That(await wrong.Content.ReadAsStringAsync()).Contains(">Start over for a new code</a>");
        }

        using (var created = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up/verify", new(verify) { ["code"] = code }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await created.Content.ReadAsStringAsync()).Contains("You are signed in.");
            await Assert.That(OidcHarness.SessionCookie(created)).IsNotNull();
        }

        // The new account signs in with its password; the step without one only asks for it.
        using (var step = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/password", new() { ["email"] = Address }))
        {
            await Assert.That(await step.Content.ReadAsStringAsync()).Contains("id=\"sign-in-password\"");
            await Assert.That(OidcHarness.SessionCookie(step)).IsNull();
        }

        await Assert.That(await PasswordSignInAsync(oidc, Address, "wrong password!")).IsNull();
        await Assert.That(await PasswordSignInAsync(oidc, Address, Password)).IsNotNull();

        // Reset: the mail start cooldown is per address, so let it pass first.
        clock.Advance(TimeSpan.FromSeconds(61));
        using (var reset = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset", new() { ["email"] = Address }))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await reset.Content.ReadAsStringAsync()).Contains("<h1>Choose a new password</h1>");
        }

        var resetCode = await oidc.Email.NextCodeAsync(Address);
        using (var done = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new() { ["email"] = Address, ["code"] = resetCode, ["password"] = "a brand new passphrase" }))
        {
            await Assert.That(done.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await done.Content.ReadAsStringAsync()).Contains("<h1>Password updated</h1>");
        }

        await Assert.That(await PasswordSignInAsync(oidc, Address, Password)).IsNull();
        await Assert.That(await PasswordSignInAsync(oidc, Address, "a brand new passphrase")).IsNotNull();
    }

    [Test]
    public async Task A_third_party_cannot_attach_a_password_to_someone_elses_sign_up()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Address = "frank@example.test";
        var owner = await SignUpAsync(oidc, Address, "the owner's password");
        var ownerCode = await oidc.Email.NextCodeAsync(Address);

        // A stranger signs the same address up with their own password: the owner's inbox receives the new code.
        clock.Advance(HostedAuth.MailCooldown);
        var stranger = await SignUpAsync(oidc, Address, "the stranger's password");
        var strangerCode = await oidc.Email.NextCodeAsync(Address);

        // The owner, redeeming the newest code with their own token, cannot complete the stranger's sign-up; their
        // own code was superseded. The stranger holds the token but not the inbox, so a guess fails too.
        foreach (var (code, token) in new[] { (strangerCode, owner), (ownerCode, owner), (strangerCode == "000000" ? "111111" : "000000", stranger) })
        {
            using var refused = await SignUpVerifyAsync(oidc, Address, code, token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        }

        // Signing up again gives the owner a fresh code and token, and the account gets the owner's password only.
        clock.Advance(HostedAuth.MailCooldown);
        var again = await SignUpAsync(oidc, Address, "the owner's password");
        using (var created = await SignUpVerifyAsync(oidc, Address, await oidc.Email.NextCodeAsync(Address), again))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await PasswordSignInAsync(oidc, Address, "the stranger's password")).IsNull();
        await Assert.That(await PasswordSignInAsync(oidc, Address, "the owner's password")).IsNotNull();
    }

    [Test]
    public async Task Answers_never_tell_an_account_from_a_stranger()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        const string Password = "correct horse battery";
        await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(OidcIssuer.InstanceId, "alice@example.test", Passwords.Hash(Password), CancellationToken.None);
        await oidc.CreateUserAsync("nopassword@example.test");

        // Unknown address, wrong password, an account without a password and a malformed address are one failure.
        var pages = new List<string>();
        foreach (var (address, password) in new[] { ("nobody@example.test", Password), ("alice@example.test", Password + "x"), ("nopassword@example.test", Password), ("not-an-email", Password) })
        {
            using var refused = await oidc.PostFormAsync($"{SignIn}/password", new() { ["email"] = address, ["password"] = password, ["next"] = string.Empty });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
            pages.Add(await OidcHarness.PageAsync(refused, address));
        }

        await Assert.That(pages.Distinct().Count()).IsEqualTo(1);

        // Signing up with a taken address mails that account a sign-in code and keeps its password.
        var pending = await SignUpAsync(oidc, "alice@example.test", "another password");
        var (recipient, mail) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("alice@example.test");
        await Assert.That(mail.Subject).Contains("sign-in code");
        using (var signedIn = await SignUpVerifyAsync(oidc, "alice@example.test", Regex.Match(mail.Text, @"\b[0-9]{6}\b").Value, pending))
        {
            await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await PasswordSignInAsync(oidc, "alice@example.test", Password)).IsNotNull();
        await Assert.That(await PasswordSignInAsync(oidc, "alice@example.test", "another password")).IsNull();

        // A reset start mails only real accounts, with the same page; completing one for an unknown address fails like a wrong code.
        using (var unknownStart = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset", new() { ["email"] = "nobody@example.test" }))
        {
            await Assert.That(unknownStart.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await unknownStart.Content.ReadAsStringAsync()).Contains("<h1>Choose a new password</h1>");
        }

        using var unknownReset = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new() { ["email"] = "nobody@example.test", ["code"] = "123456", ["password"] = "brand new password" });
        await Assert.That(unknownReset.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task A_password_reset_code_works_once_for_its_purpose_and_signs_the_user_out_everywhere()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Address = "alice@example.test";
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync(Address);
        var earlier = tokens.GetProperty("refresh_token").GetString()!;

        clock.Advance(HostedAuth.MailCooldown);
        using (var start = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset", new() { ["email"] = Address }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var (_, mail) = await oidc.Email.NextAsync();
        await Assert.That(mail.Subject).Contains("password reset code");
        var code = Regex.Match(mail.Text, @"\b[0-9]{6}\b").Value;
        var complete = new Dictionary<string, string> { ["email"] = Address, ["code"] = code, ["password"] = "brand new password" };

        // A reset code never signs in, and a password outside the policy keeps the code alive.
        using (var notSignIn = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = Address, ["code"] = code, ["next"] = string.Empty }))
        {
            await Assert.That(notSignIn.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        foreach (var weak in new[] { "short", "fourteen chars", "passwordpassword", new string('x', PasswordPolicy.MaxCharacters + 1) })
        {
            using var refused = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new(complete) { ["password"] = weak });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var reset = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", complete))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var again = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new(complete) { ["password"] = "a third password" }))
        {
            await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using (var stale = await oidc.RefreshAsync(earlier))
        {
            await Assert.That(await OidcHarness.ErrorAsync(stale)).IsEqualTo("invalid_grant");
        }

        // A sign-in code cannot reset the password either.
        clock.Advance(HostedAuth.MailCooldown);
        using (var signInStart = await oidc.PostFormAsync(SignIn, new() { ["email"] = Address, ["next"] = string.Empty }))
        {
            await Assert.That(signInStart.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var signInCode = await oidc.Email.NextCodeAsync(Address);
        using (var wrongPurpose = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new(complete) { ["code"] = signInCode, ["password"] = "a third password" }))
        {
            await Assert.That(wrongPurpose.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await Assert.That(await PasswordSignInAsync(oidc, Address, "brand new password")).IsNotNull();
    }

    [Test]
    public async Task Concurrent_redemptions_of_one_sign_up_code_across_replicas_create_one_account()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        const string Password = "correct horse battery";
        var pending = await SignUpAsync(oidc, "erin@example.test", Password);
        var code = await oidc.Email.NextCodeAsync("erin@example.test");
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            SignUpVerifyAsync(oidc, "erin@example.test", code, pending, index % 2 == 0 ? oidc.Client : replica.Client)));
        try
        {
            // One creates the account; the code then fails; the sixth attempt exceeds the email's code budget.
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(1);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized)).IsEqualTo(4);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests)).IsEqualTo(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var accounts = (await oidc.Database.ListDocumentsAsync(AuthStore.UserCollection)).Items;
        await Assert.That(accounts.Count).IsEqualTo(2);
        await Assert.That(accounts.Count(account => account.Document.GetProperty("email").GetString() == AuthTestServer.BootstrapOwnerEmail)).IsEqualTo(1);
        var signedUp = accounts.Single(account => account.Document.GetProperty("email").GetString() == "erin@example.test");
        await Assert.That(signedUp.Document.GetRawText()).DoesNotContain(Password);
        var consumed = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items.Single().Document;
        await Assert.That(consumed.TryGetProperty("passwordHash", out var hash) && hash.ValueKind != JsonValueKind.Null).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_refused_resend_keeps_code_entry_and_the_original_code_can_still_sign_in(bool known)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: new ManualClock(DateTimeOffset.UtcNow));
        const string Email = "alice@example.test";
        const string Next = "/ui/terrascale/device?user_code=ABCD&view=signin";
        if (known)
        {
            await oidc.CreateUserAsync(Email);
        }

        using var first = await oidc.PostFormAsync(SignIn, new() { ["email"] = Email, ["next"] = Next });
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var code = known ? await oidc.Email.NextCodeAsync(Email) : "000000";
        using var resend = await oidc.PostFormAsync(SignIn, new() { ["email"] = Email, ["next"] = Next });
        await Assert.That(resend.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(resend.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.MailCooldown);
        await Assert.That(resend.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(OidcHarness.SessionCookie(resend)).IsNull();
        var page = await resend.Content.ReadAsStringAsync();
        await Assert.That(page).Contains($"<form method=\"post\" action=\"{SignIn}/verify\">");
        await Assert.That(page).Contains("id=\"sign-in-code\"");
        await Assert.That(page).Contains($"name=\"email\" value=\"{Email}\"");
        await Assert.That(page).Contains($"name=\"next\" value=\"{WebUtility.HtmlEncode(Next)}\"");
        await Assert.That(page).Contains("This request sent no new code.");
        await Assert.That(page).DoesNotContain("we emailed it a six-digit code");
        await oidc.Server.App.Services.GetRequiredService<SignInMail>().StopAsync(CancellationToken.None);
        await Assert.That(oidc.Email.HasPending).IsFalse();

        // No clock advance or third mail start: the original code is usable from the returned form.
        using var verified = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = Email, ["next"] = Next, ["code"] = code });
        await Assert.That(verified.StatusCode).IsEqualTo(known ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(verified) is not null).IsEqualTo(known);
        if (known)
        {
            await Assert.That(await verified.Content.ReadAsStringAsync()).Contains($"href=\"{WebUtility.HtmlEncode(Next)}\"");
        }
    }

    [Test]
    [Arguments("sign-in")]
    [Arguments("sign-up")]
    [Arguments("reset")]
    public async Task Mail_cooldown_is_shared_across_routes_replicas_and_unknown_addresses_for_sixty_elapsed_seconds(string firstPath)
    {
        // Start one millisecond before a fixed minute boundary: crossing it must not reset the cooldown.
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60000 * 60000 + 59999));
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        await oidc.CreateUserAsync("known@example.test");

        foreach (var email in new[] { "known@example.test", "unknown@example.test" })
        {
            using (var first = await MailStartAsync(oidc, firstPath, email))
            {
                await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            if (email == "known@example.test" || firstPath == "sign-up")
            {
                await oidc.Email.NextCodeAsync(email);
            }

            var before = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items;
            foreach (var path in new[] { "sign-in", "sign-up", "reset" })
            {
                using var repeated = await MailStartAsync(oidc, path, $" {email.ToUpperInvariant()} ", replica.Client);
                await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(repeated.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.MailCooldown);
                await Assert.That(repeated.Headers.CacheControl!.NoStore).IsTrue();
                await Assert.That(OidcHarness.SessionCookie(repeated)).IsNull();
                await Assert.That(await repeated.Content.ReadAsStringAsync()).Contains("Too many attempts");
            }

            clock.Advance(HostedAuth.MailCooldown - TimeSpan.FromMilliseconds(1));
            using (var tooSoon = await MailStartAsync(oidc, "sign-in", email))
            {
                await Assert.That(tooSoon.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(tooSoon.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            }

            var after = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items;
            await Assert.That(after.Select(item => item.Version).Order(StringComparer.Ordinal)
                .SequenceEqual(before.Select(item => item.Version).Order(StringComparer.Ordinal))).IsTrue();
            clock.Advance(TimeSpan.FromMilliseconds(1));
            using var allowed = await MailStartAsync(oidc, "sign-in", email);
            await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            if (email == "known@example.test")
            {
                await oidc.Email.NextCodeAsync(email);
            }

            // Refill the IP mail bucket for the next address; rejected starts still charge the IP budget.
            clock.Advance(HostedAuth.MailInterval * HostedAuth.MailPerIp);
        }

        await oidc.Server.App.Services.GetRequiredService<SignInMail>().StopAsync(CancellationToken.None);
        await replica.App.Services.GetRequiredService<SignInMail>().StopAsync(CancellationToken.None);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Concurrent_mail_starts_on_two_replicas_admit_one_request_across_routes()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            using var response = await MailStartAsync(oidc, index % 3 == 0 ? "reset" : index % 3 == 1 ? "sign-up" : "sign-in", "concurrent@example.test", index % 2 == 0 ? oidc.Client : replica.Client);
            return response.StatusCode;
        }));
        await Assert.That(statuses.Count(status => status == HttpStatusCode.OK)).IsEqualTo(1);
        await Assert.That(statuses.Count(status => status == HttpStatusCode.TooManyRequests)).IsEqualTo(5);
    }

    // Starts a hosted sign-up and returns the pending token its verify page carries.
    [Test]
    public async Task A_signed_in_browser_sent_to_sign_up_or_sign_in_again_resumes_the_authorization_request()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (_, challenge) = OidcHarness.Pkce();
        var authorize = OidcHarness.AuthorizePath(challenge);
        foreach (var page in new[] { "sign-up", "sign-in" })
        {
            var path = $"/ui/{OidcIssuer.Slug}/{page}?next={Uri.EscapeDataString(authorize)}";
            using (var resumed = await oidc.AuthorizeAsync(path, session))
            {
                await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
                await Assert.That(resumed.Headers.Location!.OriginalString).IsEqualTo(authorize);
            }

            foreach (var cookie in new[] { null, "not-a-session" })
            {
                using var form = await oidc.AuthorizeAsync(path, cookie);
                await Assert.That(form.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }

        // The resumed request issues a code for the existing session without another sign-in.
        await Assert.That(await oidc.CodeAsync(session, challenge)).IsNotEmpty();
    }

    private static async Task<string> SignUpAsync(OidcHarness oidc, string address, string password)
    {
        using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = address, ["password"] = password, ["next"] = string.Empty });
        return response.StatusCode == HttpStatusCode.OK
            ? PendingToken().Match(await response.Content.ReadAsStringAsync()).Groups[1].Value
            : throw new InvalidOperationException($"Sign-up answered {response.StatusCode}.");
    }

    private static Task<HttpResponseMessage> SignUpVerifyAsync(OidcHarness oidc, string address, string code, string pending, HttpClient? client = null) =>
        oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up/verify", new() { ["email"] = address, ["code"] = code, ["pending_token"] = pending, ["next"] = string.Empty }, client: client);

    // Every hosted email-code start shares the same mail cooldown.
    private static Task<HttpResponseMessage> MailStartAsync(OidcHarness oidc, string path, string address, HttpClient? client = null) =>
        oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/{path}", new() { ["email"] = address, ["password"] = "correct horse battery", ["next"] = string.Empty }, client: client);

    private static async Task<string?> PasswordSignInAsync(OidcHarness oidc, string address, string password)
    {
        using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/password", new() { ["email"] = address, ["password"] = password, ["next"] = string.Empty });
        return OidcHarness.SessionCookie(response);
    }

    [GeneratedRegex("name=\"pending_token\" value=\"([^\"]+)\"")]
    private static partial Regex PendingToken();

    [Test]
    public async Task The_sign_in_page_drops_a_next_that_is_not_this_issuers_authorize_url()
    {
        await using var server = await AuthTestServer.StartAsync();

        foreach (var next in new[] { "https://evil.example/", "//evil.example/terrascale/oauth/authorize?x=1", "/terrascale/oauth/authorize?x=1#https://evil.example/", "/terrascale/oauth/authorize?x=1\\\\evil.example/" })
        {
            using var page = await server.Client.GetAsync($"{SignIn}?next={Uri.EscapeDataString(next)}");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await page.Content.ReadAsStringAsync()).DoesNotContain("evil.example");
        }
    }

    [Test]
    public async Task The_theme_picker_sets_the_colour_scheme_follows_the_os_and_survives_reload()
    {
        await using var server = await AuthTestServer.StartAsync();
        var violations = new List<string>();
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new() { ColorScheme = ColorScheme.Light });
        var page = await OpenSignInAsync(context, server, violations);
        var picker = page.Locator("select.theme-picker");

        await Assert.That(await picker.InputValueAsync()).IsEqualTo("system");
        await ExpectSchemeAsync(page, "light");

        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await ExpectSchemeAsync(page, "dark");

        await picker.SelectOptionAsync("light");
        await ExpectSchemeAsync(page, "light");
        await page.ReloadAsync();
        await Assert.That(await picker.InputValueAsync()).IsEqualTo("light");
        await Assert.That(await page.EvaluateAsync<string?>("localStorage.getItem('ts-auth-theme:terrascale')")).IsEqualTo("light");
        await ExpectSchemeAsync(page, "light");

        // An explicit choice ignores the OS; "system" follows it again.
        await picker.SelectOptionAsync("dark");
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await ExpectSchemeAsync(page, "dark");
        await picker.SelectOptionAsync("system");
        await ExpectSchemeAsync(page, "light");
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await ExpectSchemeAsync(page, "dark");

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task The_card_is_centred_and_the_footer_sits_below_it(int width, int height)
    {
        await using var server = await AuthTestServer.StartAsync();
        var violations = new List<string>();
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await OpenSignInAsync(context, server, violations);

        var card = (await page.Locator(".card").BoundingBoxAsync())!;
        var footer = (await page.Locator(".auth-foot").BoundingBoxAsync())!;
        await Assert.That(Math.Abs(card.X + card.Width / 2 - width / 2.0)).IsLessThanOrEqualTo(1);
        await Assert.That(Math.Abs(card.Y + card.Height / 2 - height / 2.0)).IsLessThanOrEqualTo(1);
        await Assert.That(await page.Locator(".card .foot-links").CountAsync()).IsEqualTo(0);
        await Assert.That(footer.Y).IsGreaterThanOrEqualTo(card.Y + card.Height);
        // The footer links and the Lucide icons load from committed files, inside the CSP.
        await Assert.That(await page.Locator(".foot-links a").CountAsync()).IsEqualTo(4);
        await Assert.That(await page.Locator("svg.icon use").First.GetAttributeAsync("href")).StartsWith("/admin/icons.svg#i-");
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task Long_email_password_steps_and_errors_fit_the_viewport(int width, int height)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var violations = new List<string>();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        await page.Locator("#sign-in-email").FillAsync("very-long-customer-address+mobile-proof@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Use a password instead" }).ClickAsync();
        await page.Locator("#sign-in-password").WaitForAsync();
        foreach (var error in new[] { false, true })
        {
            if (error)
            {
                await page.Locator("#sign-in-password").FillAsync("deliberately invalid password");
                await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
                await Assert.That(await page.Locator(".alert").TextContentAsync()).Contains("Wrong email or password");
            }

            foreach (var mode in new[] { "light", "dark" })
            {
                await page.Locator(".theme-picker").SelectOptionAsync(mode);
                var card = (await page.Locator(".card").BoundingBoxAsync())!;
                await Assert.That(card.X).IsGreaterThanOrEqualTo(0);
                await Assert.That(card.X + card.Width).IsLessThanOrEqualTo(width);
                await Assert.That(await page.EvaluateAsync<int>("document.documentElement.scrollWidth")).IsLessThanOrEqualTo(width);
            }
        }

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Six_digit_entry_submits_the_real_code_and_password_visibility_is_accessible()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var violations = new List<string>();
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        await page.Locator("#sign-in-email").FillAsync("alice@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code", Exact = true }).ClickAsync();
        var code = await oidc.Email.NextCodeAsync("alice@example.test");
        await page.Locator(".otp input").First.FillAsync(code);
        await Assert.That(await page.Locator("input[name=code]").InputValueAsync()).IsEqualTo(code);
        await Assert.That(await page.Locator(".otp input").CountAsync()).IsEqualTo(6);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and sign in" }).ClickAsync();
        await Assert.That(await page.Locator("h1").TextContentAsync()).IsEqualTo("Signed in");
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/terrascale/sign-up");
        await page.Locator("#sign-up-password").FillAsync("synthetic test password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Show password" }).ClickAsync();
        await Assert.That(await page.Locator("#sign-up-password").GetAttributeAsync("type")).IsEqualTo("text");
        await page.GetByRole(AriaRole.Button, new() { Name = "Hide password" }).ClickAsync();
        await Assert.That(await page.Locator("#sign-up-password").GetAttributeAsync("type")).IsEqualTo("password");
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments("#a3e635", null, null)]
    [Arguments("#ffffff", null, null)]
    [Arguments("#000000", null, null)]
    [Arguments("#a3e635", "#fafafa", null)]
    [Arguments("#ffffff", "#fafafa", "#ffffff")]
    [Arguments("#000000", "#0a0a0a", "#151515")]
    [Arguments("#e11d48", "#777777", "#777777")]
    [Arguments("#6d4aff", "#fce7f3", "#164e63")]
    public async Task Project_theme_reaches_every_page_and_text_remains_readable(string accent, string? text, string? background)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync(new Faker().Internet.Email().ToLowerInvariant());
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Nightforge");
        var other = await oidc.CreateProjectAsync(token, organization, "Other project");
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", token, new
        {
            branding = new
            {
                theme_color = accent,
                text_color = text,
                background_color = background,
                title_size = 32,
                default_theme = "dark",
                footer_links = new[] { new { label = "Support", url = "https://example.test/support" }, new { label = "Sign in", url = OidcHarness.PublicOrigin + "/ui/" + project + "/sign-in" } }
            },
        });
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync(new() { ColorScheme = ColorScheme.Light });
        var violations = new List<string>();
        var page = await OpenSignInAsync(context, oidc.Server, violations);
        foreach (var path in new[] { "sign-in", "sign-up", "reset" })
        {
            await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/{project}/{path}");
            await ExpectSchemeAsync(page, "dark");
            await Assert.That(await page.Locator("h1").EvaluateAsync<string>("el => getComputedStyle(el).fontSize")).IsEqualTo("32px");
            await Assert.That(await page.Locator(".foot-links a").CountAsync()).IsEqualTo(2);
            await Assert.That(await page.Locator(".foot-links a").First.GetAttributeAsync("href")).IsEqualTo("https://example.test/support");
            await Assert.That(await page.Locator(".foot-links a").First.GetAttributeAsync("target")).IsEqualTo("_blank");
            await Assert.That(await page.Locator(".foot-links a").Last.GetAttributeAsync("target")).IsNull();
            await Assert.That(await page.Locator(".foot-links a").Last.GetAttributeAsync("rel")).IsNull();

            foreach (var mode in new[] { "light", "dark" })
            {
                await page.Locator(".theme-picker").SelectOptionAsync(mode);
                await ExpectSchemeAsync(page, mode);
                await ExpectReadableTextAsync(page);
                if (background is not null)
                {
                    await Assert.That(await page.EvaluateAsync<string>("getComputedStyle(document.body).getPropertyValue('--page-background').trim()"))
                        .IsEqualTo(background);
                }

                // A safe customer colour survives unchanged in its compatible scheme.
                if (text == "#fafafa" && mode == "dark")
                {
                    await Assert.That(await page.Locator("h1").EvaluateAsync<string>("el => getComputedStyle(el).color")).IsEqualTo("rgb(250, 250, 250)");
                }
            }
        }

        // The password step's chip uses a different surface from the card and page.
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/{project}/sign-in");
        await page.Locator("#sign-in-email").FillAsync("reader@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Use a password instead" }).ClickAsync();
        await page.Locator(".chip").WaitForAsync();
        foreach (var mode in new[] { "light", "dark" })
        {
            await page.Locator(".theme-picker").SelectOptionAsync(mode);
            await ExpectSchemeAsync(page, mode);
            await ExpectReadableTextAsync(page);
            await page.Locator("button.secondary").HoverAsync();
            await ExpectReadableTextAsync(page);
        }

        await page.Locator(".theme-picker").SelectOptionAsync("light");
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/{other}/sign-in");
        await Assert.That(await page.Locator(".theme-picker").InputValueAsync()).IsEqualTo("system");
        await page.GotoAsync($"{OidcHarness.PublicOrigin}/ui/{project}/sign-in");
        await Assert.That(await page.Locator(".theme-picker").InputValueAsync()).IsEqualTo("light");
        await Assert.That(violations).IsEmpty();
    }

    private static async Task ExpectReadableTextAsync(IPage page)
    {
        var contrast = await page.EvaluateAsync<double[]>("""
            () => {
              const lum = c => { const v = c.match(/[\d.]+/g).slice(0,3).map(Number).map(v => v / 255).map(v => v <= .03928 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4); return .2126*v[0]+.7152*v[1]+.0722*v[2]; };
              const ratio = (a,b) => (Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);
              const background = el => {
                for (; el; el = el.parentElement) {
                  const color = getComputedStyle(el).backgroundColor;
                  if (color !== 'rgba(0, 0, 0, 0)') return color;
                }
                throw new Error('No painted background');
              };
              const results = [];
              for (const selector of ['.brand span', 'h1', '.sub', '.card label', '.card input:not([type=hidden])', '.card button', '.card a', '.hint', '.alt', '.chip span', '.foot-links a', '.auth-powered', '.auth-powered b', '.theme-picker']) {
                document.querySelectorAll(selector).forEach((el, index) => {
                  results.push(ratio(getComputedStyle(el).color, background(el)));
                  if (el.matches('input[placeholder]')) results.push(ratio(getComputedStyle(el, '::placeholder').color, background(el)));
                });
              }
              return results;
            }
            """);
        await Assert.That(contrast).IsNotEmpty();
        for (var index = 0; index < contrast.Length; index++)
        {
            await Assert.That(contrast[index]).IsGreaterThanOrEqualTo(4.5).Because($"{page.Url}: contrast measurement {index}");
        }
    }

    /// <summary>
    /// Opens the real sign-in page. The browser never reaches the loopback listener directly: every request to the
    /// public origin is answered by the test server with its real headers, including the CSP, and anything else is refused.
    /// </summary>
    private static async Task<IPage> OpenSignInAsync(IBrowserContext context, AuthTestServer server, List<string> violations)
    {
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (url.Host != "auth.test.invalid")
            {
                await route.AbortAsync();
                return;
            }

            using var request = new HttpRequestMessage(new HttpMethod(route.Request.Method), url.PathAndQuery);
            if (route.Request.PostDataBuffer is { } body)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", route.Request.Headers["content-type"]);
            }

            foreach (var header in route.Request.Headers.Where(header => header.Key is "origin" or "cookie"))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await server.Client.SendAsync(request);
            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(header => header.Key is not ("Content-Length" or "Transfer-Encoding"))
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value));
            await route.FulfillAsync(new() { Status = (int)response.StatusCode, Headers = headers, BodyBytes = await response.Content.ReadAsByteArrayAsync() });
        });
        var page = await context.NewPageAsync();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.Ordinal))
            {
                violations.Add(message.Text);
            }
        };
        await page.GotoAsync($"{OidcHarness.PublicOrigin}{SignIn}");
        return page;
    }

    /// <summary>Waits for the effective colour scheme, so the OS change event has run before the assertion.</summary>
    private static async Task ExpectSchemeAsync(IPage page, string expected)
    {
        await page.WaitForFunctionAsync("scheme => getComputedStyle(document.documentElement).colorScheme === scheme", expected);
        await Assert.That(await page.EvaluateAsync<string>("getComputedStyle(document.documentElement).colorScheme")).IsEqualTo(expected);
    }
}
