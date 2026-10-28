using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthThrottleTests(TerraBaseFixture fixture)
{
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";
    private const string SignUp = $"/ui/{OidcIssuer.Slug}/sign-up";
    private const string Reset = $"/ui/{OidcIssuer.Slug}/reset";
    private const string Next = $"/{OidcIssuer.Slug}/oauth/authorize?client_id=01M4EQ2FRJ8S8B29E8TQVWNW4J&response_type=code";

    private static ManualClock Clock() => new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600));

    private static string UnknownEmail(OidcHarness oidc)
    {
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        return Enumerable.Range(0, 128).Select(index => $"stranger{index}@example.test")
            .First(email => limiter.Fold(email) != limiter.Fold("alice@example.test"));
    }

    [Test]
    public async Task Code_attempts_share_an_email_budget_across_replicas_and_sign_in_surfaces()
    {
        var clock = Clock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        await oidc.CreateUserAsync();

        foreach (var email in new[] { "alice@example.test", UnknownEmail(oidc) })
        {
            for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
            {
                // Sign-in and sign-up verification spend the same budget.
                using var wrong = await oidc.PostFormAsync(attempt % 2 == 0 ? $"{SignIn}/verify" : $"{SignUp}/verify", new() { ["email"] = email, ["code"] = "000000" }, client: replica.Client);
                await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            using var limited = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = email.ToUpperInvariant(), ["code"] = "123456", ["next"] = Next });
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(limited.Headers.RetryAfter!.Delta > TimeSpan.Zero).IsTrue();
            await Assert.That(limited.Headers.CacheControl!.NoStore).IsTrue();
            await Assert.That(OidcHarness.SessionCookie(limited)).IsNull();
            var page = await limited.Content.ReadAsStringAsync();
            await Assert.That(page).Contains($"<form method=\"post\" action=\"{SignIn}/verify\">");
            await Assert.That(page).Contains("id=\"sign-in-code\"");
            await Assert.That(page).DoesNotContain("id=\"sign-in-email\"");
            await Assert.That(page).Contains($"name=\"email\" value=\"{email.ToUpperInvariant()}\"");
            await Assert.That(page).Contains($"name=\"next\" value=\"{WebUtility.HtmlEncode(Next)}\"");
            await Assert.That(page).Contains($"<strong>Too many attempts</strong><p>Try again in {limited.Headers.GetValues("Retry-After").Single()} seconds.</p>");

            // The reset budget is independent, even for the same normalized email.
            using var recovery = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = "not-a-code", ["password"] = "new password" });
            await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Mail_budget_applies_to_unknown_emails_and_is_shared_by_hosted_codes_and_password_reset()
    {
        var clock = Clock();
        var windowStart = clock.GetUtcNow();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();

        foreach (var email in new[] { "alice@example.test", UnknownEmail(oidc) })
        {
            for (var attempt = 0; attempt < Passcodes.MaxIssuesPerWindow; attempt++)
            {
                // All mail starts share the cooldown and still spend the same hourly budget.
                if (attempt > 0)
                {
                    clock.Advance(HostedAuth.MailCooldown);
                }

                using var start = await oidc.PostFormAsync(attempt == 2 ? Reset : SignIn, new() { ["email"] = attempt == 1 ? email.ToUpperInvariant() : email });
                await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
                if (email == "alice@example.test")
                {
                    await oidc.Email.NextCodeAsync(email);
                }
            }

            clock.Advance(HostedAuth.MailCooldown);
            var remaining = Passcodes.IssueWindow - (clock.GetUtcNow() - windowStart);
            using var hosted = await oidc.PostFormAsync(SignIn, new() { ["email"] = email.ToUpperInvariant(), ["next"] = Next });
            await Assert.That(hosted.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(hosted.Headers.RetryAfter!.Delta).IsEqualTo(remaining);
            await Assert.That(hosted.Headers.CacheControl!.NoStore).IsTrue();
            await Assert.That(OidcHarness.SessionCookie(hosted)).IsNull();
            var page = await hosted.Content.ReadAsStringAsync();
            await Assert.That(page).Contains($"<form method=\"post\" action=\"{SignIn}\">");
            await Assert.That(page).Contains($"action=\"{SignIn}/verify\"");
            await Assert.That(page).DoesNotContain("id=\"sign-in-email\"");
            await Assert.That(page).Contains("id=\"sign-in-code\"");
            await Assert.That(page).Contains("This request sent no new code.");
            await Assert.That(page).DoesNotContain("we emailed it a six-digit code");
            await Assert.That(page).Contains($"value=\"{email.ToUpperInvariant()}\"");
            await Assert.That(page).Contains($"name=\"next\" value=\"{WebUtility.HtmlEncode(Next)}\"");
            await Assert.That(page).Contains($"<strong>Too many attempts</strong><p>Try again in {hosted.Headers.GetValues("Retry-After").Single()} seconds.</p>");
            clock.Advance(HostedAuth.MailCooldown);
            using var reset = await oidc.PostFormAsync(Reset, new() { ["email"] = email });
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(reset.Headers.RetryAfter!.Delta).IsEqualTo(remaining - HostedAuth.MailCooldown);
        }
    }

    [Test]
    public async Task Restarting_a_pending_sign_up_does_not_reset_the_shared_code_attempt_budget()
    {
        var clock = Clock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        const string email = "pending@example.test";
        var pending = await StartSignUpAsync(oidc, email);
        var code = await oidc.Email.NextCodeAsync(email);
        var wrong = code == "000000" ? "111111" : "000000";
        for (var attempt = 0; attempt < HostedAuth.CodePerEmail - 1; attempt++)
        {
            using var refused = await oidc.PostFormAsync($"{SignUp}/verify", new() { ["email"] = email, ["code"] = wrong, ["pending_token"] = pending }, client: replica.Client);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        clock.Advance(HostedAuth.MailCooldown);
        pending = await StartSignUpAsync(oidc, email);
        code = await oidc.Email.NextCodeAsync(email);
        using (var fifth = await oidc.PostFormAsync($"{SignUp}/verify", new() { ["email"] = email, ["code"] = code == "000000" ? "111111" : "000000", ["pending_token"] = pending }))
        {
            await Assert.That(fifth.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using (var limited = await oidc.PostFormAsync($"{SignUp}/verify", new() { ["email"] = email.ToUpperInvariant(), ["code"] = code, ["pending_token"] = pending }, client: replica.Client))
        {
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(limited.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.CodeWindow - HostedAuth.MailCooldown);
            await Assert.That(OidcHarness.SessionCookie(limited)).IsNull();
        }

        clock.Advance(HostedAuth.CodeWindow - HostedAuth.MailCooldown);
        pending = await StartSignUpAsync(oidc, email);
        using var created = await oidc.PostFormAsync($"{SignUp}/verify", new() { ["email"] = email, ["code"] = await oidc.Email.NextCodeAsync(email), ["pending_token"] = pending }, client: replica.Client);
        await Assert.That(OidcHarness.SessionCookie(created)).IsNotNull();
    }

    private static async Task<string> StartSignUpAsync(OidcHarness oidc, string email)
    {
        using var signUp = await oidc.PostFormAsync(SignUp, new() { ["email"] = email, ["password"] = "correct horse battery" });
        await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return Regex.Match(await signUp.Content.ReadAsStringAsync(), "name=\"pending_token\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    [Test]
    public async Task Reset_attempts_are_limited_before_hashing_even_without_a_live_code_and_resume_next_window()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)HostedAuth.CodeWindow.TotalSeconds * (long)HostedAuth.CodeWindow.TotalSeconds));
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();
        foreach (var email in new[] { "alice@example.test", UnknownEmail(oidc) })
        {
            for (var attempt = 0; attempt < HostedAuth.CodePerEmail; attempt++)
            {
                using var wrong = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = "not-a-code", ["password"] = "new password" });
                await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            using var limited = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = "123456", ["password"] = "new password" });
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(limited.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.CodeWindow);
        }

        clock.Advance(HostedAuth.CodeWindow);
        using var renewed = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = UnknownEmail(oidc), ["code"] = "not-a-code", ["password"] = "new password" });
        await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Hosted_calls_have_an_ip_budget_and_ignore_spoofed_forwarding_headers()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: Clock());
        for (var attempt = 0; attempt < HostedAuth.PerIp; attempt++)
        {
            using var counted = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = $"stranger{attempt}@example.test", ["code"] = "000000" });
            await Assert.That(counted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, SignIn)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "another@example.test" }),
        };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        request.Headers.Add("X-Forwarded-For", "203.0.113.77");
        using var refused = await oidc.Client.SendAsync(request);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(refused.Headers.RetryAfter!.Delta > TimeSpan.Zero).IsTrue();
        await Assert.That(refused.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        var page = await refused.Content.ReadAsStringAsync();
        await Assert.That(page).Contains($"<form method=\"post\" action=\"{SignIn}\">");
        await Assert.That(page).Contains($"action=\"{SignIn}/verify\"");
        await Assert.That(page).DoesNotContain("id=\"sign-in-email\"");
        await Assert.That(page).Contains("id=\"sign-in-code\"");
        await Assert.That(page).Contains("This request sent no new code.");
        await Assert.That(page).DoesNotContain("we emailed it a six-digit code");
        await Assert.That(page).Contains($"<strong>Too many attempts</strong><p>Try again in {refused.Headers.GetValues("Retry-After").Single()} seconds.</p>");
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Hosted_sign_in_fails_closed_without_rate_limit_storage_and_sends_no_code()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: Clock());
        await oidc.CreateUserAsync();
        await oidc.Database.DropCollectionAsync("rate_limits");
        using var refused = await oidc.PostFormAsync(SignIn, new() { ["email"] = "alice@example.test" });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(oidc.Email.HasPending).IsFalse();
        await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
    }

    [Test]
    public async Task The_ip_mail_bucket_refills_and_refused_retries_never_extend_the_wait()
    {
        var clock = Clock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var emails = DistinctEmails(oidc, HostedAuth.MailPerIp + 2);
        for (var attempt = 0; attempt < HostedAuth.MailPerIp; attempt++)
        {
            using var started = await oidc.PostFormAsync(SignIn, new() { ["email"] = emails[attempt] });
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var step = TimeSpan.FromSeconds(10);
        for (var retry = 0; retry < 5; retry++)
        {
            // Each refused retry finds a shorter wait: refusals spend nothing.
            using var refused = await oidc.PostFormAsync(SignIn, new() { ["email"] = emails[HostedAuth.MailPerIp] });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.MailInterval - step * retry);
            clock.Advance(step);
        }

        clock.Advance(HostedAuth.MailInterval - step * 5);
        using (var refilled = await oidc.PostFormAsync(SignIn, new() { ["email"] = emails[HostedAuth.MailPerIp] }))
        {
            await Assert.That(refilled.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var spent = await oidc.PostFormAsync(SignIn, new() { ["email"] = emails[HostedAuth.MailPerIp + 1] });
        await Assert.That(spent.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(spent.Headers.RetryAfter!.Delta).IsEqualTo(HostedAuth.MailInterval);
    }

    [Test]
    public async Task Under_ts_orch_the_loopback_edge_names_each_client_so_one_clients_burst_never_limits_another()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: Clock(), configure: builder => builder.Configuration["TS_NODE"] = "ts-gru-1");
        var emails = DistinctEmails(oidc, HostedAuth.MailPerIp + 2);
        for (var attempt = 0; attempt <= HostedAuth.MailPerIp; attempt++)
        {
            using var office = await StartFromAsync(oidc, "203.0.113.10", emails[attempt]);
            await Assert.That(office.StatusCode).IsEqualTo(attempt < HostedAuth.MailPerIp ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests);
        }

        using var elsewhere = await StartFromAsync(oidc, "198.51.100.20", emails[HostedAuth.MailPerIp + 1]);
        await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // The edge replaces X-Forwarded-For with the client's socket address before it reaches the node on loopback.
    private static Task<HttpResponseMessage> StartFromAsync(OidcHarness oidc, string client, string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SignIn) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }) };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        request.Headers.Add("X-Forwarded-For", client);
        return oidc.Client.SendAsync(request);
    }

    // Unknown addresses whose keyed per-email buckets never collide, so only the IP buckets refuse.
    private static string[] DistinctEmails(OidcHarness oidc, int count)
    {
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        return [.. Enumerable.Range(0, 4096).Select(index => $"person{index}@example.test").DistinctBy(limiter.Fold).Take(count)];
    }

    [Test]
    public async Task A_code_superseded_by_a_resend_never_signs_in()
    {
        var clock = Clock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();
        using (var first = await oidc.PostFormAsync(SignIn, new() { ["email"] = "alice@example.test" }))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var superseded = await oidc.Email.NextCodeAsync("alice@example.test");
        clock.Advance(HostedAuth.MailCooldown);
        using (var resend = await oidc.PostFormAsync(SignIn, new() { ["email"] = "alice@example.test" }))
        {
            await Assert.That(resend.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var newest = await oidc.Email.NextCodeAsync("alice@example.test");
        // QA round 4, auth:5: the replaced code signed in. Codes collide one time in a million.
        if (superseded != newest)
        {
            using var stale = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = superseded });
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(stale)).IsNull();
        }

        using var current = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = newest });
        await Assert.That(OidcHarness.SessionCookie(current)).IsNotNull();
    }
}
