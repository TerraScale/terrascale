using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Account lockout notices over real HTTP, with durable claims shared through the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class LockoutTests(TerraBaseFixture fixture)
{
    private const string Password = "correct horse battery";
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";
    private const string Reset = $"/ui/{OidcIssuer.Slug}/reset";

    [Test]
    public async Task Password_refusals_match_unknown_accounts_notify_once_across_replicas_and_notify_again_next_window()
    {
        var start = WindowStart();
        var clock = new ManualClock(start.AddSeconds(123).AddMilliseconds(125));
        var deadline = start + HostedAuth.PasswordWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var email = Address();
        await CreatePasswordUserAsync(oidc, email);
        var unknown = DistinctEmails(oidc, 1, email).Single();

        foreach (var address in new[] { email, unknown })
        {
            for (var attempt = 0; attempt < HostedAuth.PasswordPerEmail; attempt++)
            {
                using var refused = await PasswordAsync(oidc, address, "wrong password", replica.Client);
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
        }

        // Spending the last admission does not notify; the first refusal does.
        await DrainAsync(oidc.Server, replica);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline)).Count).IsEqualTo(0);
        var wait = TimeSpan.FromSeconds(Math.Ceiling((deadline - clock.GetUtcNow()).TotalSeconds));
        // Known and unknown addresses get the same refusal; pages differ only in the echoed address and CSP nonce.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            using var refused = await PasswordAsync(oidc, index % 2 == 0 ? email : unknown, Password, index % 4 < 2 ? oidc.Client : replica.Client);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(wait);
            await Assert.That(refused.Headers.CacheControl!.NoStore).IsTrue();
            await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("<strong>Too many attempts</strong>");
        }));
        await DrainAsync(oidc.Server, replica);
        await AssertNoticeAsync(oidc, email, deadline);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        var first = (await NoticeClaimsAsync(oidc, deadline)).Single();
        await Assert.That(first.Value.Until).IsEqualTo(deadline.ToUnixTimeSeconds());

        // A new service instance must see the claim, not only the limiter bucket.
        await using var restarted = await oidc.StartReplicaAsync(clock: clock);
        using (var duplicate = await PasswordAsync(oidc, email.ToUpperInvariant(), Password, restarted.Client))
        {
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        await DrainAsync(restarted);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline)).Single().Value.Version).IsEqualTo(first.Value.Version);
        clock.Advance(deadline - clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        using (var early = await PasswordAsync(oidc, email, Password))
        {
            await Assert.That(early.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(early.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        for (var attempt = 0; attempt < HostedAuth.PasswordPerEmail; attempt++)
        {
            using var admitted = await PasswordAsync(oidc, email, "wrong password", restarted.Client);
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await DrainAsync(restarted);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using (var nextEpisode = await PasswordAsync(oidc, email, Password, replica.Client))
        {
            await Assert.That(nextEpisode.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(nextEpisode.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.PasswordWindow);
        }

        await DrainAsync(replica);
        await AssertNoticeAsync(oidc, email, deadline + HostedAuth.PasswordWindow);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        var renewed = (await NoticeClaimsAsync(oidc, deadline, deadline + HostedAuth.PasswordWindow)).Single();
        await Assert.That(renewed.Key).IsEqualTo(first.Key);
        await Assert.That(renewed.Value.Version).IsNotEqualTo(first.Value.Version);
        await Assert.That(renewed.Value.Until).IsEqualTo((deadline + HostedAuth.PasswordWindow).ToUnixTimeSeconds());
    }

    [Test]
    public async Task An_older_notice_delayed_on_a_slow_replica_cannot_replace_or_repeat_a_newer_episode()
    {
        var windowStart = WindowStart();
        var start = windowStart.AddSeconds(1);
        var slowClock = new ManualClock(start);
        var clock = new ManualClock(start);
        var deadline = windowStart + HostedAuth.CodeWindow;
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
            for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
            {
                using var admitted = await CodeAsync(oidc, email, "000000");
                await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            // The old refusal is queued behind the blocked worker, with its original persisted deadline.
            using (var old = await CodeAsync(oidc, email, "000000"))
            {
                await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(old.Headers.RetryAfter!.Delta).IsEqualTo(deadline - start);
            }

            await Assert.That(oidc.Email.HasPending).IsFalse();
            clock.Advance(deadline - clock.GetUtcNow());
            for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
            {
                using var admitted = await CodeAsync(oidc, email, "000000", replica.Client);
                await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            using (var current = await CodeAsync(oidc, email, "000000", replica.Client))
            {
                await Assert.That(current.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            }

            await DrainAsync(replica);
            await AssertNoticeAsync(oidc, email, deadline + HostedAuth.CodeWindow);
            var newest = (await NoticeClaimsAsync(oidc, deadline, deadline + HostedAuth.CodeWindow)).Single();
            release.SetResult(null);
            await DrainAsync(oidc.Server);
            await Assert.That(oidc.Email.HasPending).IsFalse();
            await Assert.That((await NoticeClaimsAsync(oidc, deadline, deadline + HostedAuth.CodeWindow)).Single().Value.Version).IsEqualTo(newest.Value.Version);

            // The slow clock sees the newer persisted limiter window and must not claim another notice.
            using var slow = await CodeAsync(oidc, email, "000000");
            await Assert.That(slow.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(slow.Headers.RetryAfter!.Delta).IsEqualTo(deadline + HostedAuth.CodeWindow - start);
            await DrainAsync(oidc.Server);
            await Assert.That(oidc.Email.HasPending).IsFalse();
            await Assert.That((await NoticeClaimsAsync(oidc, deadline, deadline + HostedAuth.CodeWindow)).Single().Value.Version).IsEqualTo(newest.Value.Version);
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
        var deadline = clock.GetUtcNow() + HostedAuth.CodeWindow;
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
                for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
                {
                    using var admitted = await CodeAsync(oidc, address, "000000");
                    await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                }
            }

            using (var firstNotice = await CodeAsync(oidc, email, "000000"))
            {
                await Assert.That(firstNotice.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            }

            await WaitForSendsAsync(sender, 1);
            // More than a queue's capacity, mixing known and unknown addresses while the sender cannot make progress.
            // Every call comes from one socket peer, so the clock moves on to refill its IP bucket; the code window stays.
            for (var attempt = 0; attempt < SignInMail.Capacity + 2; attempt++)
            {
                if (attempt % 100 == 0)
                {
                    clock.Advance(HostedAuth.IpInterval * 100);
                }

                using var refused = await CodeAsync(oidc, attempt % 2 == 0 ? email : unknown, "000000");
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(deadline - clock.GetUtcNow());
            }

            using (var recovery = await oidc.PostFormAsync(Reset, new() { ["email"] = email }))
            {
                await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            // The recovery worker reaches the sender without waiting for the notice worker or using its full queue.
            await WaitForSendsAsync(sender, 2);
            gate.SetResult();
            var messages = await Task.WhenAll(sender.NextAsync(), sender.NextAsync());
            await Assert.That(messages.All(message => message.Recipient == email)).IsTrue();
            await Assert.That(messages.Count(message => message.Message.Subject == "Temporary authentication pause for TerraScale")).IsEqualTo(1);
            var code = messages.Single(message => message.Message.Subject.EndsWith(" is your TerraScale password reset code", StringComparison.Ordinal)).Message.Subject[..6];
            using var reset = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = code, ["password"] = Password });
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
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
        var advancing = new ExpiringEmailConfigHandler(clock) { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock,
            services: services => services.AddSingleton(new HttpClient(advancing)));
        var email = Address();
        await oidc.CreateUserAsync(email);
        for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
        {
            using var admitted = await CodeAsync(oidc, email, "000000");
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using (var refused = await CodeAsync(oidc, email, "000000"))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        await DrainAsync(oidc.Server);
        await Assert.That(advancing.Calls).IsEqualTo(1);
        await Assert.That(clock.GetUtcNow()).IsEqualTo(start + HostedAuth.CodeWindow);
        await Assert.That((await NoticeClaimsAsync(oidc, clock.GetUtcNow())).Count).IsEqualTo(1);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Sign_in_and_sign_up_codes_share_attempts_but_notice_claims_use_actual_accounts_and_hosted_locale()
    {
        var start = WindowStart();
        var clock = new ManualClock(start);
        var deadline = start + HostedAuth.CodeWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var (email, collision) = CollidingEmails(oidc);
        await oidc.CreateUserAsync(email);
        await oidc.CreateUserAsync(collision);
        await Assert.That(oidc.Server.App.Services.GetRequiredService<AttemptLimiter>().Fold(email))
            .IsEqualTo(oidc.Server.App.Services.GetRequiredService<AttemptLimiter>().Fold(collision));
        for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
        {
            using var admitted = attempt % 2 == 0
                ? await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up/verify", new() { ["email"] = email, ["code"] = "000000" }, client: replica.Client)
                : await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = email.ToUpperInvariant(), ["code"] = "000000" });
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
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
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.CodeWindow);
            await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        }

        await DrainAsync(oidc.Server);
        await AssertNoticeAsync(oidc, email, deadline, portuguese: true);
        using (var otherAccount = await CodeAsync(oidc, collision, "000000", replica.Client))
        {
            await Assert.That(otherAccount.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(otherAccount.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.CodeWindow);
        }

        await DrainAsync(replica);
        await AssertNoticeAsync(oidc, collision, deadline);
        var claims = await NoticeClaimsAsync(oidc, deadline);
        await Assert.That(claims.Count).IsEqualTo(2);
        using (var duplicate = await CodeAsync(oidc, email, "000000", replica.Client))
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
        using (var recovery = await oidc.PostFormAsync(Reset, new() { ["email"] = email }))
        {
            await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var code = await oidc.Email.NextCodeAsync(email);
        using var recovered = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = code, ["password"] = Password });
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await DrainAsync(oidc.Server, replica);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Reset_code_lockout_notifies_once_without_locking_password_or_sign_in_codes()
    {
        var start = WindowStart();
        var clock = new ManualClock(start);
        var deadline = start + HostedAuth.CodeWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var email = Address();
        await CreatePasswordUserAsync(oidc, email);
        for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
        {
            using var admitted = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = "not-a-code", ["password"] = Password });
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        for (var refusal = 0; refusal < 2; refusal++)
        {
            using var locked = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = "123456", ["password"] = Password });
            await Assert.That(locked.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(locked.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.CodeWindow);
        }

        await DrainAsync(oidc.Server);
        await AssertNoticeAsync(oidc, email, deadline);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline)).Count).IsEqualTo(1);
        using (var password = await PasswordAsync(oidc, email, Password))
        {
            await Assert.That(OidcHarness.SessionCookie(password)).IsNotNull();
        }

        using (var startCode = await oidc.PostFormAsync(SignIn, new() { ["email"] = email }))
        {
            await Assert.That(startCode.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var codeSignIn = await CodeAsync(oidc, email, await oidc.Email.NextCodeAsync(email));
        await Assert.That(OidcHarness.SessionCookie(codeSignIn)).IsNotNull();
        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task IP_and_mail_issuance_refusals_never_send_account_lockout_notices()
    {
        var start = WindowStart();
        var clock = new ManualClock(start);
        var deadline = start + HostedAuth.CodeWindow;
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var email = Address();
        await oidc.CreateUserAsync(email);
        var unknown = DistinctEmails(oidc, HostedAuth.PerIp + HostedAuth.MailPerIp, email);
        await Assert.That(unknown.Length).IsEqualTo(HostedAuth.PerIp + HostedAuth.MailPerIp);
        // Every call comes from one socket peer: later steps wait for its IP bucket, then its mail bucket, to refill.
        for (var attempt = 0; attempt < HostedAuth.PerIp; attempt++)
        {
            using var admitted = await CodeAsync(oidc, unknown[attempt], "000000");
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using (var ipRefusal = await PasswordAsync(oidc, email, Password))
        {
            await Assert.That(ipRefusal.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(ipRefusal.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.IpInterval);
        }

        clock.Advance(HostedAuth.IpInterval * HostedAuth.PerIp);
        for (var attempt = 0; attempt < HostedAuth.MailPerIp; attempt++)
        {
            using var admitted = await oidc.PostFormAsync(Reset, new() { ["email"] = unknown[HostedAuth.PerIp + attempt] });
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var ipMailRefusal = await oidc.PostFormAsync(SignIn, new() { ["email"] = email }))
        {
            await Assert.That(ipMailRefusal.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(ipMailRefusal.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.MailInterval);
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        // A full mail bucket, so the per-email cooldown and hourly budgets below answer on their own.
        clock.Advance(HostedAuth.MailInterval * HostedAuth.MailPerIp);
        using (var first = await oidc.PostFormAsync(Reset, new() { ["email"] = email }))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await oidc.Email.NextCodeAsync(email);
        using (var cooldown = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = email, ["password"] = Password }))
        {
            await Assert.That(cooldown.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(cooldown.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.MailCooldown);
        }

        for (var attempt = 1; attempt < Passcodes.MaxIssuesPerWindow; attempt++)
        {
            clock.Advance(HostedAuth.MailCooldown);
            using var issued = await oidc.PostFormAsync(Reset, new() { ["email"] = email });
            await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await oidc.Email.NextCodeAsync(email);
        }

        clock.Advance(HostedAuth.MailCooldown);
        using (var hourly = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = email, ["password"] = Password }))
        {
            await Assert.That(hourly.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(hourly.Headers.RetryAfter!.Delta).IsEqualTo(start + Passcodes.IssueWindow - clock.GetUtcNow());
        }

        await DrainAsync(oidc.Server);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That((await NoticeClaimsAsync(oidc, deadline, deadline + HostedAuth.CodeWindow)).Count).IsEqualTo(0);
    }

    private static Task<HttpResponseMessage> PasswordAsync(OidcHarness oidc, string email, string password, HttpClient? client = null) =>
        oidc.PostFormAsync($"{SignIn}/password", new() { ["email"] = email, ["password"] = password }, client: client);

    private static Task<HttpResponseMessage> CodeAsync(OidcHarness oidc, string email, string code, HttpClient? client = null) =>
        oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = email, ["code"] = code }, client: client);

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
                clock.Advance(HostedAuth.CodeWindow);
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
        var drained = server.DrainMailAsync();
        var noticesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.App.Services.GetRequiredService<SignInMail>().EnqueueLockout(() =>
        {
            noticesDrained.SetResult();
            return Task.FromResult<AuthEmailValues?>(null);
        });
        await Task.WhenAll(drained, noticesDrained.Task).WaitAsync(TimeSpan.FromSeconds(60));
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
