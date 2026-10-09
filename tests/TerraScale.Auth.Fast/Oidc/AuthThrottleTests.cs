using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthThrottleTests(TerraBaseFixture fixture)
{
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";
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
            for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
            {
                using var wrong = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, "000000", $"198.51.100.{attempt}", client: replica.Client);
                await Assert.That(await OidcHarness.ErrorAsync(wrong)).IsEqualTo("invalid_grant");
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
            await Assert.That(page).Contains($"<p class=\"error\" role=\"alert\">Too many attempts. Try again in {limited.Headers.GetValues("Retry-After").Single()} seconds.</p>");

            // The reset budget is independent, even for the same normalized email.
            using var recovery = await oidc.HeadlessAsync("password-reset/complete", new { email, code = "not-a-code", new_password = "new password", ip_address = "192.0.2.5" });
            await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(recovery)).IsEqualTo("invalid_code");
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
                // Hosted mail has no headless cooldown, but still spends the same hourly budget.
                if (attempt > 1)
                {
                    clock.Advance(HeadlessAuth.MailCooldown);
                }

                var path = attempt == 2 ? "password-reset/start" : "email-code/start";
                using var start = attempt == 1
                    ? await oidc.PostFormAsync(SignIn, new() { ["email"] = email.ToUpperInvariant() })
                    : await oidc.HeadlessAsync(path, new { email, ip_address = $"198.51.100.{attempt}" });
                await Assert.That(start.StatusCode).IsEqualTo(attempt == 1 ? HttpStatusCode.OK : HttpStatusCode.Accepted);
                if (email == "alice@example.test")
                {
                    await oidc.Email.NextCodeAsync(email);
                }
            }

            clock.Advance(HeadlessAuth.MailCooldown);
            var remaining = Passcodes.IssueWindow - (clock.GetUtcNow() - windowStart);
            using var hosted = await oidc.PostFormAsync(SignIn, new() { ["email"] = email.ToUpperInvariant(), ["next"] = Next });
            await Assert.That(hosted.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(hosted.Headers.RetryAfter!.Delta).IsEqualTo(remaining);
            await Assert.That(hosted.Headers.CacheControl!.NoStore).IsTrue();
            await Assert.That(OidcHarness.SessionCookie(hosted)).IsNull();
            var page = await hosted.Content.ReadAsStringAsync();
            await Assert.That(page).Contains($"<form method=\"post\" action=\"{SignIn}\">");
            await Assert.That(page).DoesNotContain($"action=\"{SignIn}/verify\"");
            await Assert.That(page).Contains("id=\"sign-in-email\"");
            await Assert.That(page).DoesNotContain("id=\"sign-in-code\"");
            await Assert.That(page).DoesNotContain("Check your email");
            await Assert.That(page).DoesNotContain("we emailed it a six-digit code");
            await Assert.That(page).Contains($"value=\"{email.ToUpperInvariant()}\"");
            await Assert.That(page).Contains($"name=\"next\" value=\"{WebUtility.HtmlEncode(Next)}\"");
            await Assert.That(page).Contains($"<p class=\"error\" role=\"alert\">Too many attempts. Try again in {hosted.Headers.GetValues("Retry-After").Single()} seconds.</p>");
            using var reset = await oidc.HeadlessAsync("password-reset/start", new { email, ip_address = "192.0.2.8" });
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(reset)).IsEqualTo("rate_limited");
            await Assert.That(reset.Headers.RetryAfter!.Delta).IsEqualTo(remaining);
        }
    }

    [Test]
    public async Task Resending_a_pending_signup_does_not_reset_the_shared_code_attempt_budget()
    {
        var clock = Clock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        const string email = "pending@example.test";
        using var signup = await oidc.HeadlessAsync("sign-up", new { email, password = "correct horse battery", ip_address = "192.0.2.1" });
        await Assert.That(signup.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var pending = (await OidcHarness.JsonAsync(signup)).GetProperty("pending_token").GetString()!;
        var code = await oidc.Email.NextCodeAsync(email);
        var wrong = code == "000000" ? "111111" : "000000";
        for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail - 1; attempt++)
        {
            using var refused = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, wrong, $"198.51.100.{attempt}", client: replica.Client, pendingToken: pending);
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
        }

        clock.Advance(HeadlessAuth.MailCooldown);
        using (var resend = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = "192.0.2.2", pending_token = pending }))
        {
            await Assert.That(resend.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        code = await oidc.Email.NextCodeAsync(email);
        using (var fifth = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, code == "000000" ? "111111" : "000000", "198.51.100.5", pendingToken: pending))
        {
            await Assert.That(await OidcHarness.ErrorAsync(fifth)).IsEqualTo("invalid_grant");
        }

        using (var limited = await oidc.GrantAsync(HeadlessGrants.EmailCode, email.ToUpperInvariant(), code, "192.0.2.3", client: replica.Client, pendingToken: pending))
        {
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await OidcHarness.ErrorAsync(limited)).IsEqualTo("rate_limited");
            await Assert.That(limited.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.CodeWindow - HeadlessAuth.MailCooldown);
        }

        clock.Advance(HeadlessAuth.CodeWindow - HeadlessAuth.MailCooldown);
        using (var renewed = await oidc.HeadlessAsync("email-code/start", new { email, ip_address = "192.0.2.4", pending_token = pending }))
        {
            await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        using var created = await oidc.GrantAsync(HeadlessGrants.EmailCode, email, await oidc.Email.NextCodeAsync(email), "192.0.2.4", client: replica.Client, pendingToken: pending);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Reset_attempts_are_limited_before_hashing_even_without_a_live_code_and_resume_next_window()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 900 * 900));
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();
        foreach (var email in new[] { "alice@example.test", UnknownEmail(oidc) })
        {
            for (var attempt = 0; attempt < HeadlessAuth.CodePerEmail; attempt++)
            {
                using var wrong = await oidc.HeadlessAsync("password-reset/complete", new { email, code = "not-a-code", new_password = "new password", ip_address = $"198.51.100.{attempt}" });
                await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using var limited = await oidc.HeadlessAsync("password-reset/complete", new { email, code = "123456", new_password = "new password", ip_address = "192.0.2.2" });
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(limited.Headers.RetryAfter!.Delta).IsEqualTo(HeadlessAuth.CodeWindow);
        }

        clock.Advance(HeadlessAuth.CodeWindow);
        using var renewed = await oidc.HeadlessAsync("password-reset/complete", new { email = UnknownEmail(oidc), code = "not-a-code", new_password = "new password", ip_address = "192.0.2.2" });
        await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Hosted_calls_have_an_ip_budget_and_ignore_spoofed_forwarding_headers()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: Clock());
        for (var attempt = 0; attempt < HeadlessAuth.PerIp; attempt++)
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
        await Assert.That(page).DoesNotContain($"action=\"{SignIn}/verify\"");
        await Assert.That(page).Contains("id=\"sign-in-email\"");
        await Assert.That(page).DoesNotContain("id=\"sign-in-code\"");
        await Assert.That(page).DoesNotContain("Check your email");
        await Assert.That(page).DoesNotContain("we emailed it a six-digit code");
        await Assert.That(page).Contains($"<p class=\"error\" role=\"alert\">Too many attempts. Try again in {refused.Headers.GetValues("Retry-After").Single()} seconds.</p>");
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
}
