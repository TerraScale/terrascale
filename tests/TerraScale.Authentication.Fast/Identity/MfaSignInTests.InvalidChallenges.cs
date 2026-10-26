using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

public sealed partial class MfaSignInTests
{
    [Test]
    [Arguments("forged")]
    [Arguments("expired")]
    [Arguments("other-tenant")]
    [Arguments("stale")]
    [Arguments("refused-origin")]
    public async Task Invalid_or_refused_challenges_open_no_session_and_preserve_an_unused_recovery_proof(string rejection)
    {
        var faker = new Faker();
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var path = MfaPath;
        if (rejection == "other-tenant")
        {
            // Use a real existing tenant: a nonexistent slug could fail before challenge validation.
            var (_, ownerToken) = await oidc.SignInUserAsync(faker.Internet.Email().ToLowerInvariant());
            var project = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), faker.Commerce.ProductName());
            using var page = await oidc.Client.GetAsync($"/ui/{project}/sign-in");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            path = $"/ui/{project}/sign-in/mfa";
        }

        var user = await oidc.CreateUserAsync(faker.Internet.Email().ToLowerInvariant());
        var (_, codes, _) = await EnrolAsync(oidc, await oidc.SignInAsync(user.Email), clock);
        clock.UtcNow += HostedAuth.MailCooldown;
        var challenge = await ChallengeAsync(oidc, user.Email);
        await Assert.That(challenge).IsNotEmpty();
        if (rejection == "forged")
        {
            // Keep the current v1 box shape valid while corrupting its authentication tag.
            var box = Convert.FromBase64String(challenge[3..]);
            box[^1] ^= 1;
            challenge = "v1:" + Convert.ToBase64String(box);
        }
        else if (rejection == "expired")
        {
            // Challenge lifetime is five minutes; advancing the harness clock never sleeps.
            clock.UtcNow += TimeSpan.FromMinutes(5);
        }
        else if (rejection == "stale")
        {
            // Change the real persisted auth epoch while keeping the active factor and its recovery codes.
            await Assert.That(await oidc.Server.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(user.InstanceId, user.Id)).IsTrue();
        }

        var before = await SnapshotAsync(oidc);
        using var refused = await oidc.PostFormAsync(path, new() { ["challenge"] = challenge, ["code"] = codes[0] },
            origin: rejection == "refused-origin" ? "https://other.example.test" : OidcHarness.PublicOrigin);
        await Assert.That(refused.StatusCode).IsEqualTo(rejection == "refused-origin" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        await Assert.That(refused.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith("tsauth_session", StringComparison.Ordinal))).IsFalse();
        if (rejection != "refused-origin")
        {
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Sign-in expired");
        }

        var after = await SnapshotAsync(oidc);
        await Assert.That(after.Factors).IsEqualTo(before.Factors);
        await Assert.That(after.Users).IsEqualTo(before.Users);
        await Assert.That(after.Sessions).IsEquivalentTo(before.Sessions);

        clock.UtcNow += HostedAuth.MailCooldown;
        var freshChallenge = await ChallengeAsync(oidc, user.Email);
        await Assert.That(freshChallenge).IsNotEmpty();
        using var fresh = await oidc.PostFormAsync(MfaPath, new() { ["challenge"] = freshChallenge, ["code"] = codes[0] });
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var session = OidcHarness.SessionCookie(fresh);
        await Assert.That(session).IsNotNull();
        using var security = await SendAsync(oidc, HttpMethod.Get, "/ui/terrascale/security", session!);
        await Assert.That(security.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await security.Content.ReadAsStringAsync()).Contains("On (authenticator app)");
        await Assert.That((await SnapshotAsync(oidc)).Sessions.Length).IsEqualTo(before.Sessions.Length + 1);
    }
}
