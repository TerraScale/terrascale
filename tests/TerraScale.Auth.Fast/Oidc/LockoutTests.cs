using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Features.SignIn;
using TerraScale.Auth.Security;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Oidc;

/// <summary>Account lockout notices over real HTTP, with durable claims shared through the pinned TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class LockoutTests(TerraBaseFixture fixture)
{
    private const string Password = "correct horse battery";
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";

    [Test]
    public async Task Password_refusals_match_unknown_accounts_notify_once_across_replicas_and_notify_again_next_window()
    {
        var start = WindowStart();
        var clock = new ManualClock(start.AddSeconds(123).AddMilliseconds(125));
        var deadline = start + HeadlessAuth.PasswordWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var email = Address();
        await CreatePasswordUserAsync(oidc, email);
        var unknown = DistinctEmails(oidc, 1, email).Single();

        foreach (var address in new[] { email, unknown })
        {
            for (var attempt = 0; attempt < HeadlessAuth.PasswordPerEmail; attempt++)
            {
                using var refused = await oidc.GrantAsync(HeadlessGrants.Password, address, "wrong password", $"198.51.100.{attempt}", client: replica.Client);
                await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
            }
        }

        // Spending the last admission does not notify; the first refusal does.
        await DrainAsync(oidc.Server, replica);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline)).Count).IsEqualTo(0);
        var wait = TimeSpan.FromSeconds(Math.Ceiling((deadline - clock.GetUtcNow()).TotalSeconds));
        var bodies = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            using var refused = await oidc.GrantAsync(HeadlessGrants.Password, index % 2 == 0 ? email : unknown,
                Password, $"203.0.113.{index}", client: index % 4 < 2 ? oidc.Client : replica.Client);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("rate_limited");
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(wait);
            await Assert.That(refused.Headers.CacheControl!.NoStore).IsTrue();
            await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
            return await refused.Content.ReadAsStringAsync();
        }));
        await Assert.That(bodies.Distinct().Count()).IsEqualTo(1);
        await DrainAsync(oidc.Server, replica);
        await AssertNoticeAsync(oidc, email, deadline);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        var first = (await NoticeClaimsAsync(oidc, deadline)).Single();
        await Assert.That(first.Value.Until).IsEqualTo(deadline.ToUnixTimeSeconds());

        // A new service instance must see the claim, not only the limiter bucket.
        await using var restarted = await oidc.StartReplicaAsync(clock: clock);
        using (var duplicate = await oidc.GrantAsync(HeadlessGrants.Password, email.ToUpperInvariant(), Password, "192.0.2.10", client: restarted.Client))
        {
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        await DrainAsync(restarted);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline)).Single().Value.Version).IsEqualTo(first.Value.Version);
        clock.Advance(deadline - clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        using (var early = await oidc.GrantAsync(HeadlessGrants.Password, email, Password, "192.0.2.10"))
        {
            await Assert.That(early.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(early.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        for (var attempt = 0; attempt < HeadlessAuth.PasswordPerEmail; attempt++)
        {
            using var admitted = await oidc.GrantAsync(HeadlessGrants.Password, email, "wrong password", $"198.51.100.{attempt}", client: restarted.Client);
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.ErrorAsync(admitted)).IsEqualTo("invalid_grant");
        }

        await DrainAsync(restarted);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using (var nextEpisode = await oidc.GrantAsync(HeadlessGrants.Password, email, Password, "192.0.2.10", client: replica.Client))
        {
            await Assert.That(nextEpisode.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(nextEpisode.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.PasswordWindow);
        }

        await DrainAsync(replica);
        await AssertNoticeAsync(oidc, email, deadline + HeadlessAuth.PasswordWindow);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        var renewed = (await NoticeClaimsAsync(oidc, deadline, deadline + HeadlessAuth.PasswordWindow)).Single();
        await Assert.That(renewed.Key).IsEqualTo(first.Key);
        await Assert.That(renewed.Value.Version).IsNotEqualTo(first.Value.Version);
        await Assert.That(renewed.Value.Until).IsEqualTo((deadline + HeadlessAuth.PasswordWindow).ToUnixTimeSeconds());
    }

    [Test]
    public async Task An_older_notice_delayed_on_a_slow_replica_cannot_replace_or_repeat_a_newer_episode()
    {
        var windowStart = WindowStart();
        var start = windowStart.AddSeconds(1);
        var slowClock = new ManualClock(start);
        var clock = new ManualClock(start);
        var deadline = windowStart + HeadlessAuth.CodeWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: slowClock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var email = Address();
        await oidc.CreateUserAsync(email);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthEmailValues?>(TaskCreationOptions.RunContinuationsAsynchronously);
        oidc.Server.App.Services.GetRequiredService<SignInMail>().EnqueueLockout(() =>
        {
            entered.SetResult();
            return release.Task;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
            {
                using var admitted = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", $"198.51.100.{attempt}");
                await Assert.That(await OidcHarness.ErrorAsync(admitted)).IsEqualTo("invalid_grant");
            }

            // The old refusal is queued behind the blocked worker, with its original persisted deadline.
            using (var old = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", "192.0.2.1"))
            {
                await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(old.Headers.RetryAfter!.Delta).IsEqualTo(deadline - start);
            }

            await Assert.That(oidc.Email.HasPending).IsFalse();
            clock.Advance(deadline - clock.GetUtcNow());
            for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
            {
                using var admitted = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", $"198.51.100.{attempt}", client: replica.Client);
                await Assert.That(await OidcHarness.ErrorAsync(admitted)).IsEqualTo("invalid_grant");
            }

            using (var current = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", "192.0.2.1", client: replica.Client))
            {
                await Assert.That(current.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            }

            await DrainAsync(replica);
            await AssertNoticeAsync(oidc, email, deadline + HeadlessAuth.CodeWindow);
            var newest = (await NoticeClaimsAsync(oidc, deadline, deadline + HeadlessAuth.CodeWindow)).Single();
            release.SetResult(null);
            await DrainAsync(oidc.Server);
            await Assert.That(oidc.Email.HasPending).IsFalse();
            await Assert.That((await NoticeClaimsAsync(oidc, deadline, deadline + HeadlessAuth.CodeWindow)).Single().Value.Version).IsEqualTo(newest.Value.Version);

            // The slow clock sees the newer persisted limiter window and must not claim another notice.
            using var slow = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", "192.0.2.2");
            await Assert.That(slow.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(slow.Headers.RetryAfter!.Delta).IsEqualTo(deadline + HeadlessAuth.CodeWindow - start);
            await DrainAsync(oidc.Server);
            await Assert.That(oidc.Email.HasPending).IsFalse();
            await Assert.That((await NoticeClaimsAsync(oidc, deadline, deadline + HeadlessAuth.CodeWindow)).Single().Value.Version).IsEqualTo(newest.Value.Version);
        }
        finally
        {
            release.TrySetResult(null);
        }
    }

    [Test]
    public async Task A_flood_of_known_and_unknown_lockout_refusals_cannot_drop_recovery_mail_behind_a_stalled_notice()
    {
        var clock = new ManualClock(WindowStart());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new CapturingEmailSender { Gate = gate };
        await using var oidc = await OidcHarness.StartAsync(fixture, email: sender, clock: clock);
        var email = Address();
        await oidc.CreateUserAsync(email);
        var unknown = DistinctEmails(oidc, 1, email).Single();
        try
        {
            foreach (var address in new[] { email, unknown })
            {
                for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
                {
                    using var admitted = await oidc.GrantAsync(HeadlessGrants.EmailCode, address, "000000", $"198.51.100.{attempt}");
                    await Assert.That(await OidcHarness.ErrorAsync(admitted)).IsEqualTo("invalid_grant");
                }
            }

            using (var firstNotice = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", "192.0.2.1"))
            {
                await Assert.That(firstNotice.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            }

            await WaitForSendsAsync(sender, 1);
            // More than a queue's capacity, mixing known and unknown addresses while SMTP cannot make progress.
            for (var attempt = 0; attempt < SignInMail.Capacity + 2; attempt++)
            {
                using var refused = await oidc.GrantAsync(HeadlessGrants.EmailCode, attempt % 2 == 0 ? email : unknown,
                    "000000", $"198.18.{attempt / 256}.{attempt % 256}");
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.CodeWindow);
            }

            using (var recovery = await oidc.HeadlessAsync("password-reset/start", new { email, ip_address = "192.0.2.2" }))
            {
                await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            }

            // The recovery worker reaches SMTP without waiting for the notice worker or using its full queue.
            await WaitForSendsAsync(sender, 2);
            gate.SetResult();
            var messages = await Task.WhenAll(sender.NextAsync(), sender.NextAsync());
            await Assert.That(messages.All(message => message.Recipient == email)).IsTrue();
            await Assert.That(messages.Count(message => message.Message.Subject == "Temporary authentication pause for TerraScale")).IsEqualTo(1);
            var code = messages.Single(message => message.Message.Subject.EndsWith(" is your TerraScale password reset code", StringComparison.Ordinal)).Message.Subject[..6];
            using var reset = await oidc.HeadlessAsync("password-reset/complete", new { email, code, new_password = Password, ip_address = "192.0.2.2" });
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await oidc.Server.App.StopAsync();
            await Assert.That(sender.HasPending).IsFalse();
            await Assert.That(sender.MaxInFlight).IsEqualTo(2);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Test]
    public async Task A_notice_that_expires_during_email_configuration_is_not_sent()
    {
        var start = WindowStart();
        var clock = new ManualClock(start);
        var advancing = new ExpiringEmailConfigHandler(clock) { InnerHandler = fixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock,
            services: services => services.AddSingleton(new HttpClient(advancing)));
        var email = Address();
        await oidc.CreateUserAsync(email);
        for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
        {
            using var admitted = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", $"198.51.100.{attempt}");
            await Assert.That(await OidcHarness.ErrorAsync(admitted)).IsEqualTo("invalid_grant");
        }

        using (var refused = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", "192.0.2.1"))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        await DrainAsync(oidc.Server);
        await Assert.That(advancing.Calls).IsEqualTo(1);
        await Assert.That(clock.GetUtcNow()).IsEqualTo(start + HeadlessAuth.CodeWindow);
        await Assert.That((await NoticeClaimsAsync(oidc, clock.GetUtcNow())).Count).IsEqualTo(1);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Hosted_and_headless_codes_share_attempts_but_notice_claims_use_actual_accounts_and_hosted_locale()
    {
        var start = WindowStart();
        var clock = new ManualClock(start);
        var deadline = start + HeadlessAuth.CodeWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var (email, collision) = CollidingEmails(oidc);
        await oidc.CreateUserAsync(email);
        await oidc.CreateUserAsync(collision);
        await Assert.That(oidc.Server.App.Services.GetRequiredService<AttemptLimiter>().Fold(email))
            .IsEqualTo(oidc.Server.App.Services.GetRequiredService<AttemptLimiter>().Fold(collision));
        for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
        {
            using var admitted = attempt % 2 == 0
                ? await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", $"198.51.100.{attempt}", client: replica.Client)
                : await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = email.ToUpperInvariant(), ["code"] = "000000" });
            await Assert.That(admitted.StatusCode).IsEqualTo(attempt % 2 == 0 ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized);
        }

        await DrainAsync(oidc.Server, replica);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{SignIn}/verify")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = $" {email.ToUpperInvariant()} ", ["code"] = "000000" }),
        })
        {
            request.Headers.Add("Origin", OidcHarness.PublicOrigin);
            request.Headers.Add("Accept-Language", "pt-BR,pt;q=0.9,en;q=0.5");
            using var refused = await oidc.Client.SendAsync(request);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.CodeWindow);
            await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        }

        await DrainAsync(oidc.Server);
        await AssertNoticeAsync(oidc, email, deadline, portuguese: true);
        using (var otherAccount = await oidc.GrantAsync(HeadlessGrants.EmailCode, collision, "000000", "192.0.2.1", client: replica.Client))
        {
            await Assert.That(otherAccount.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(otherAccount.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.CodeWindow);
        }

        await DrainAsync(replica);
        await AssertNoticeAsync(oidc, collision, deadline);
        var claims = await NoticeClaimsAsync(oidc, deadline);
        await Assert.That(claims.Count).IsEqualTo(2);
        using (var duplicate = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", "192.0.2.2", client: replica.Client))
        {
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        await DrainAsync(replica);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        foreach (var claim in await NoticeClaimsAsync(oidc, deadline))
        {
            await Assert.That(claim.Value.Version).IsEqualTo(claims[claim.Key].Version);
        }

        // Recovery still issues and redeems a real code while sign-in code redemption is locked.
        using (var recovery = await oidc.HeadlessAsync("password-reset/start", new { email, ip_address = "192.0.2.3" }))
        {
            await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        var code = await oidc.Email.NextCodeAsync(email);
        using var recovered = await oidc.HeadlessAsync("password-reset/complete", new { email, code, new_password = Password, ip_address = "192.0.2.3" });
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await DrainAsync(oidc.Server, replica);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Reset_code_lockout_notifies_once_without_locking_password_or_sign_in_codes()
    {
        var start = WindowStart();
        var clock = new ManualClock(start);
        var deadline = start + HeadlessAuth.CodeWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var email = Address();
        await CreatePasswordUserAsync(oidc, email);
        for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
        {
            using var admitted = await oidc.HeadlessAsync("password-reset/complete", new { email, code = "not-a-code", new_password = Password, ip_address = $"198.51.100.{attempt}" });
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(admitted)).IsEqualTo("invalid_code");
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        for (var refusal = 0; refusal < 2; refusal++)
        {
            using var locked = await oidc.HeadlessAsync("password-reset/complete", new { email, code = "123456", new_password = Password, ip_address = "192.0.2.1" });
            await Assert.That(locked.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(locked)).IsEqualTo("rate_limited");
            await Assert.That(locked.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.CodeWindow);
        }

        await DrainAsync(oidc.Server);
        await AssertNoticeAsync(oidc, email, deadline);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline)).Count).IsEqualTo(1);
        using (var password = await oidc.GrantAsync(HeadlessGrants.Password, email, Password, "192.0.2.2"))
        {
            await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var startCode = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = "192.0.2.2" }))
        {
            await Assert.That(startCode.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        using var codeSignIn = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, await oidc.Email.NextCodeAsync(email), "192.0.2.2");
        await Assert.That(codeSignIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task IP_and_mail_issuance_refusals_never_send_account_lockout_notices()
    {
        var start = WindowStart();
        var clock = new ManualClock(start);
        var deadline = start + HeadlessAuth.CodeWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var email = Address();
        await oidc.CreateUserAsync(email);
        var unknown = DistinctEmails(oidc, HeadlessAuth.PerIp + HeadlessAuth.MailPerIp, email);
        await Assert.That(unknown.Length).IsEqualTo(HeadlessAuth.PerIp + HeadlessAuth.MailPerIp);
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        // Exhausted IP folds must not overlap the later mail/cooldown checks.
        var ips = Enumerable.Range(1, 128).Select(index => $"203.0.113.{index}")
            .Where(ip => limiter.Fold(ip) != limiter.Fold("198.51.100.1")).DistinctBy(limiter.Fold).Take(2).ToArray();
        for (var attempt = 0; attempt < HeadlessAuth.PerIp; attempt++)
        {
            using var admitted = await oidc.GrantAsync(HeadlessGrants.EmailCode, unknown[attempt], "000000", "198.51.100.1");
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var ipRefusal = await oidc.GrantAsync(HeadlessGrants.Password, email, Password, "198.51.100.1"))
        {
            await Assert.That(ipRefusal.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(ipRefusal.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromMinutes(1));
        }

        for (var attempt = 0; attempt < HeadlessAuth.MailPerIp; attempt++)
        {
            using var admitted = await oidc.HeadlessAsync("password-reset/start", new { email = unknown[HeadlessAuth.PerIp + attempt], ip_address = ips[0] });
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        using (var ipMailRefusal = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = ips[0] }))
        {
            await Assert.That(ipMailRefusal.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(ipMailRefusal.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.MailWindow);
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using (var first = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = ips[1] }))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        await oidc.Email.NextCodeAsync(email);
        using (var cooldown = await oidc.HeadlessAsync("password-reset/start", new { email, ip_address = ips[1] }))
        {
            await Assert.That(cooldown.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(cooldown.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.MailCooldown);
        }

        for (var attempt = 1; attempt < Passcodes.MaxIssuesPerWindow; attempt++)
        {
            clock.Advance(HeadlessAuth.MailCooldown);
            using var issued = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = ips[1] });
            await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await oidc.Email.NextCodeAsync(email);
        }

        clock.Advance(HeadlessAuth.MailCooldown);
        using (var hourly = await oidc.HeadlessAsync("password-reset/start", new { email, ip_address = ips[1] }))
        {
            await Assert.That(hourly.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(hourly.Headers.RetryAfter!.Delta).IsEqualTo(start + Passcodes.IssueWindow - clock.GetUtcNow());
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline)).Count).IsEqualTo(0);
    }

    private static DateTimeOffset WindowStart() =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600);

    private static async Task WaitForSendsAsync(CapturingEmailSender sender, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (sender.InFlight < expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ExpiringEmailConfigHandler(ManualClock clock) : DelegatingHandler
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.Contains("/email_configs/", StringComparison.Ordinal)
                && Interlocked.Increment(ref this.calls) == 1)
            {
                clock.Advance(HeadlessAuth.CodeWindow);
            }

            return response;
        }
    }

    private static string Address() => $"lockout-{Guid.NewGuid():N}@example.test";

    private static async Task CreatePasswordUserAsync(OidcHarness oidc, string email) =>
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(OidcIssuer.InstanceId, email, Passwords.Hash(Password), CancellationToken.None)).IsNotNull();

    private static string[] DistinctEmails(OidcHarness oidc, int count, string excluded)
    {
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var prefix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return Enumerable.Range(0, count * 4).Select(index => $"unknown-{prefix}-{index}@example.test")
            .Where(email => limiter.Fold(email) != limiter.Fold(excluded)).DistinctBy(limiter.Fold).Take(count).ToArray();
    }

    private static (string Email, string Collision) CollidingEmails(OidcHarness oidc)
    {
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        // 65,537 candidates guarantee a collision among the 65,536 folds; normally only hundreds are needed.
        for (var index = 0; index <= ushort.MaxValue + 1; index++)
        {
            var email = $"collision-{prefix}-{index}@example.test";
            var fold = limiter.Fold(email);
            if (seen.TryGetValue(fold, out var other))
            {
                return (other, email);
            }

            seen.Add(fold, email);
        }

        throw new InvalidOperationException("No collision in the bounded fold space.");
    }

    private static Task DrainAsync(params AuthTestServer[] servers) => Task.WhenAll(servers.Select(async server =>
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.App.Services.GetRequiredService<SignInMail>().EnqueueIssue(() =>
        {
            drained.SetResult();
            return Task.FromResult<IssuedPasscode?>(null);
        });
        var noticesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.App.Services.GetRequiredService<SignInMail>().EnqueueLockout(() =>
        {
            noticesDrained.SetResult();
            return Task.FromResult<AuthEmailValues?>(null);
        });
        await Task.WhenAll(drained.Task, noticesDrained.Task).WaitAsync(TimeSpan.FromSeconds(10));
    }));

    private static async Task AssertNoticeAsync(OidcHarness oidc, string email, DateTimeOffset deadline, bool portuguese = false)
    {
        var (recipient, message) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo(email);
        await Assert.That(message.Subject).Contains(portuguese ? "Pausa temporária de autenticação" : "Temporary authentication pause");
        await Assert.That(message.Html).Contains(portuguese ? "lang=\"pt-BR\"" : "lang=\"en\"");
        await Assert.That(message.Text).Contains(email);
        await Assert.That(message.Text).Contains(deadline.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        await Assert.That(message.Text).DoesNotContain("just signed in");
        await Assert.That(message.Text).DoesNotContain("acabou de entrar");
        await Assert.That(message.Html).DoesNotContain("class=\"code\"");
    }

    private static async Task<Dictionary<string, (string Version, long Until)>> NoticeClaimsAsync(OidcHarness oidc, params DateTimeOffset[] deadlines)
    {
        // Notice claims use Unix-second deadlines in the existing collection; attempt windows use window numbers
        // and mail cooldowns use Unix milliseconds. Match the exact persisted deadline, not the rounded Retry-After.
        var records = (await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items;
        return records.Where(item => deadlines.Any(deadline => item.Document.GetProperty("window").GetInt64() == deadline.ToUnixTimeSeconds()))
            .ToDictionary(item => item.Id, item => (item.Version, item.Document.GetProperty("window").GetInt64()), StringComparer.Ordinal);
    }
}
