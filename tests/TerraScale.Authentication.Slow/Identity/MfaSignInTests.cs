using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OtpNet;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>Hosted authenticator enrolment, two-factor sign-in, recovery codes and the organization policy.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class MfaSignInTests(TerraBaseFixture fixture)
{

    /// <summary>Enrols through the hosted security pages; returns the secret, the recovery codes and the new session.</summary>
    private static async Task<(string Secret, IReadOnlyList<string> Codes, string Session)> EnrolAsync(OidcHarness oidc, string session, StoreTestClock clock)
    {
        using var begin = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/totp", session, []);
        await Assert.That(begin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var setup = await begin.Content.ReadAsStringAsync();
        await Assert.That(setup).Contains("src=\"data:image/png;base64,");
        var secret = SecretPattern().Match(setup).Groups[1].Value;
        using var confirm = await SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/totp/confirm", session, new() { ["code"] = Code(secret, clock) });
        await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var codes = RecoveryPattern().Matches(await confirm.Content.ReadAsStringAsync()).Select(match => match.Groups[1].Value).ToList();
        var enrolled = OidcHarness.SessionCookie(confirm)!;
        await Assert.That(enrolled).IsNotNull();
        clock.UtcNow += TimeSpan.FromSeconds(31);
        return (secret, codes, enrolled);
    }

    /// <summary>Signs in with an emailed code and returns the second-factor challenge the page carries.</summary>
    private static async Task<string> ChallengeAsync(OidcHarness oidc, string email)
    {
        using (var start = await oidc.PostFormAsync("/ui/terrascale/sign-in", new() { ["email"] = email }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var verify = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new() { ["email"] = email, ["code"] = await oidc.Email.NextCodeAsync(email) });
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(verify)).IsNull();
        return WebUtility.HtmlDecode(ChallengePattern().Match(await verify.Content.ReadAsStringAsync()).Groups[1].Value);
    }

    private static Task<HttpResponseMessage> SendAsync(OidcHarness oidc, HttpMethod method, string path, string session, Dictionary<string, string>? form = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        if (form is not null)
        {
            request.Headers.Add("Origin", OidcHarness.PublicOrigin);
            request.Content = new FormUrlEncodedContent(form);
        }

        return oidc.Client.SendAsync(request);
    }

    private static string Code(string secret, StoreTestClock clock) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.UtcNow.UtcDateTime);

    // A code from an hour away is outside the one-step verification window.
    private static string WrongCode(string secret, StoreTestClock clock) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.UtcNow.UtcDateTime.AddHours(1));

    [GeneratedRegex("Enter this key: <code>([A-Z2-7]+)</code>")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("<li><code>([A-Za-z0-9]{24})</code></li>")]
    private static partial Regex RecoveryPattern();

    [GeneratedRegex("name=\"challenge\" value=\"([^\"]+)\"")]
    private static partial Regex ChallengePattern();
}
