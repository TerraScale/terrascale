using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class RecoveryRegenerateTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Regenerating_recovery_codes_refuses_old_codes_keeps_new_ones_and_leaves_the_factor_enabled()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("regen@example.test");
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var session = await sessions.CreateAsync(user);
        var (secret, codes, enrolled) = await EnrolAsync(oidc, session.Token, clock);

        clock.Advance(TimeSpan.FromSeconds(31));
        string fresh;
        using (var regenerate = await SendAsync(oidc, "/ui/terrascale/security/recovery/regenerate", enrolled, new() { ["code"] = Code(secret, clock) }))
        {
            await Assert.That(regenerate.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var page = await regenerate.Content.ReadAsStringAsync();
            var issued = RecoveryPattern().Matches(page).Select(match => match.Groups[1].Value).ToList();
            await Assert.That(issued.Count).IsEqualTo(8);
            await Assert.That(issued).DoesNotContain(codes[0]);
            fresh = issued[0];
        }

        await Assert.That(await StatusAsync(oidc, enrolled)).IsEqualTo(8);
        clock.Advance(TimeSpan.FromSeconds(31));
        using (var disable = await SendAsync(oidc, "/ui/terrascale/security/totp/disable", enrolled, new() { ["code"] = codes[1] }))
        {
            await Assert.That(disable.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using (var disable = await SendAsync(oidc, "/ui/terrascale/security/totp/disable", enrolled, new() { ["code"] = fresh }))
        {
            await Assert.That(disable.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await disable.Content.ReadAsStringAsync()).Contains("Two-factor authentication is off.");
        }
    }

    [Test]
    public async Task Regenerating_recovery_codes_with_a_stale_session_asks_to_sign_in_again_without_spending_the_code()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("regen-stale@example.test");
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var session = await sessions.CreateAsync(user);
        var (secret, codes, enrolled) = await EnrolAsync(oidc, session.Token, clock);

        clock.Advance(TimeSpan.FromMinutes(16));
        using (var regenerate = await SendAsync(oidc, "/ui/terrascale/security/recovery/regenerate", enrolled, new() { ["code"] = Code(secret, clock) }))
        {
            await Assert.That(regenerate.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            var page = await regenerate.Content.ReadAsStringAsync();
            await Assert.That(page).Contains("Sign in again");
            await Assert.That(page).DoesNotContain("That code is wrong or already used");
        }

        using (var disable = await SendAsync(oidc, "/ui/terrascale/security/totp/disable", enrolled, new() { ["code"] = codes[0] }))
        {
            await Assert.That(disable.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    private static async Task<(string Secret, IReadOnlyList<string> Codes, string Session)> EnrolAsync(OidcHarness oidc, string session, ManualClock clock)
    {
        using var begin = await SendAsync(oidc, "/ui/terrascale/security/totp", session, []);
        await Assert.That(begin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var setup = await begin.Content.ReadAsStringAsync();
        await Assert.That(setup).Contains("src=\"data:image/png;base64,");
        var secret = SecretPattern().Match(setup).Groups[1].Value;
        using var confirm = await SendAsync(oidc, "/ui/terrascale/security/totp/confirm", session, new() { ["code"] = Code(secret, clock) });
        await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var codes = RecoveryPattern().Matches(await confirm.Content.ReadAsStringAsync()).Select(match => match.Groups[1].Value).ToList();
        var enrolled = OidcHarness.SessionCookie(confirm)!;
        await Assert.That(enrolled).IsNotNull();
        return (secret, codes, enrolled);
    }

    private static async Task<int> StatusAsync(OidcHarness oidc, string session)
    {
        using var page = await SendAsync(oidc, "/ui/terrascale/security", session);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var remaining = RemainingPattern().Match(await page.Content.ReadAsStringAsync()).Groups[1].Value;
        return int.Parse(remaining, CultureInfo.InvariantCulture);
    }

    private static Task<HttpResponseMessage> SendAsync(OidcHarness oidc, string path, string session, Dictionary<string, string>? form = null)
    {
        var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        if (form is not null)
        {
            request.Headers.Add("Origin", OidcHarness.PublicOrigin);
            request.Content = new FormUrlEncodedContent(form);
        }

        return oidc.Client.SendAsync(request);
    }

    private static string Code(string secret, ManualClock clock) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.GetUtcNow().UtcDateTime);

    [GeneratedRegex("<dt>Setup key</dt><dd><code>([A-Z2-7]+)</code>")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("<li><code>([A-Za-z0-9]{24})</code></li>")]
    private static partial Regex RecoveryPattern();

    [GeneratedRegex(@"<dt>Recovery codes left</dt><dd>(\d+)</dd>")]
    private static partial Regex RemainingPattern();
}
