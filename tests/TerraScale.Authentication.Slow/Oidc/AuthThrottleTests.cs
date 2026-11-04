using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Oidc;

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
            await Assert.That(page).Contains($"<strong>Too many attempts</strong><p>Try again in {SignInFlow.RetryWait(long.Parse(limited.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture))}.</p>");

            // The reset budget is independent, even for the same normalized email.
            using var recovery = await oidc.PostFormAsync($"{Reset}/complete", new() { ["email"] = email, ["code"] = "not-a-code", ["password"] = "a new long passphrase" });
            await Assert.That(recovery.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }
}
