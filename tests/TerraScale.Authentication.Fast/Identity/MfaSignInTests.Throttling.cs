using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

public sealed partial class MfaSignInTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Throttling_preserves_only_valid_MFA_continuation_and_never_consumes_the_proof(bool accountBudget)
    {
        // Each case owns its real HTTP server, database, clock and rate-limit buckets.
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("throttled@example.test");
        var session = await oidc.SignInAsync(user.Email);
        var (_, codes, _) = await EnrolAsync(oidc, session, clock);
        clock.UtcNow += HostedAuth.MailCooldown;
        if (accountBudget)
        {
            // Start a fresh account-budget window before issuing the challenge; enrolment used the old window.
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds((clock.UtcNow.ToUnixTimeSeconds() / 900 + 1) * 900);
        }
        var challenge = await ChallengeAsync(oidc, user.Email);
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        if (accountBudget)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var wrong = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = "invalid" });
                await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
        }
        else
        {
            for (var attempt = 0; attempt < HostedAuth.PerIp; attempt++)
            {
                await limiter.AcquireAsync(OidcIssuer.InstanceId, "headless-ip", limiter.Fold("127.0.0.1"), HostedAuth.PerIp,
                    TimeSpan.FromMinutes(1), CancellationToken.None);
            }
        }

        const string Next = "/ui/terrascale/security";
        using var refused = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new()
        {
            ["challenge"] = challenge,
            ["code"] = codes[0],
            ["next"] = Next,
            ["email"] = "untrusted@example.test",
        });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        var seconds = refused.Headers.RetryAfter!.Delta!.Value;
        await Assert.That(seconds.TotalSeconds).IsGreaterThan(0);
        var html = await refused.Content.ReadAsStringAsync();
        await Assert.That(html).Contains($"name=\"challenge\" value=\"{WebUtility.HtmlEncode(challenge)}\"");
        await Assert.That(html).Contains($"name=\"next\" value=\"{Next}\"");
        await Assert.That(html).Contains("id=\"retry-hint\"");
        await Assert.That(html).Contains("<time datetime=");
        await Assert.That(html).DoesNotContain(codes[0]);
        await Assert.That(html).DoesNotContain("untrusted@example.test");

        using var forged = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new()
        { ["challenge"] = "forged-challenge", ["code"] = codes[0], ["next"] = "https://evil.example" });
        await Assert.That(await forged.Content.ReadAsStringAsync()).DoesNotContain("forged-challenge");
        await Assert.That(await forged.Content.ReadAsStringAsync()).DoesNotContain("https://evil.example");

        clock.UtcNow += seconds;
        if (accountBudget)
        {
            // The 15-minute budget outlasts the 5-minute challenge: require primary authentication again.
            using var expired = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = codes[0], ["next"] = Next });
            await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(await expired.Content.ReadAsStringAsync()).Contains("Sign-in expired");
            challenge = await ChallengeAsync(oidc, user.Email);
        }

        using var recovered = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = codes[0], ["next"] = Next });
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(recovered)).IsNotNull();
    }
}
