using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class DashboardNextTests(TerraBaseFixture fixture)
{
    private static readonly string Profile = TerraScale.Authentication.Configuration.AuthOptions.DashboardOrigin + "/profile";
    private static readonly string SecurityTab = Profile + "?tab=security";

    [Test]
    public async Task A_password_reset_with_the_dashboard_security_tab_as_next_shows_a_back_to_account_link()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("reset"));
        using (var reset = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset", new() { ["email"] = user.Email, ["next"] = SecurityTab }))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await reset.Content.ReadAsStringAsync()).Contains($"name=\"next\" value=\"{SecurityTab}\"");
        }

        var code = await oidc.Email.NextCodeAsync(user.Email);
        using var done = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete",
            new() { ["email"] = user.Email, ["code"] = code, ["password"] = "a fresh passphrase 32 chars", ["next"] = SecurityTab });
        var finished = await done.Content.ReadAsStringAsync();
        await Assert.That(done.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(finished).Contains("<h1>Password updated</h1>");
        await Assert.That(finished).Contains($">Back to account</a>").And.Contains($"href=\"{SecurityTab}\"");
        await Assert.That(finished).Contains("You were signed out everywhere.");
    }

    [Test]
    public async Task A_factor_change_with_the_dashboard_profile_as_next_shows_a_back_to_account_link()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("factor"));
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var session = await sessions.CreateAsync(user);
        using (var page = await GetAsync(oidc, $"/ui/{OidcIssuer.Slug}/security?next={Uri.EscapeDataString(Profile)}", session.Token))
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Back to account");
        }

        using var begin = await PostAsync(oidc, "/ui/terrascale/security/totp", session.Token, new() { ["next"] = Profile });
        var setup = await begin.Content.ReadAsStringAsync();
        var secret = SetupKeyPattern().Match(setup).Groups[1].Value;
        using var confirm = await PostAsync(oidc, "/ui/terrascale/security/totp/confirm", OidcHarness.SessionCookie(begin) ?? session.Token,
            new() { ["code"] = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.GetUtcNow().UtcDateTime), ["next"] = Profile });
        var finished = await confirm.Content.ReadAsStringAsync();
        await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(finished).Contains("Save your recovery codes");
        await Assert.That(finished).Contains($">Back to account</a>").And.Contains($"href=\"{Profile}\"");
    }

    [Test]
    public async Task Near_misses_of_the_dashboard_profile_url_are_refused()
    {
        var oidc = await fixture.SharedAsync();
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("near"));
        foreach (var hostile in new[] { "https://evil.example/profile", TerraScale.Authentication.Configuration.AuthOptions.DashboardOrigin + ".evil.example/profile",
            TerraScale.Authentication.Configuration.AuthOptions.DashboardOrigin + "/profile/../admin", TerraScale.Authentication.Configuration.AuthOptions.DashboardOrigin + "/profile%3Ftab%3Dsecurity",
            TerraScale.Authentication.Configuration.AuthOptions.DashboardOrigin + "/profile?tab=security&x=1", TerraScale.Authentication.Configuration.AuthOptions.DashboardOrigin + "/profile#",
            TerraScale.Authentication.Configuration.AuthOptions.DashboardOrigin.ToLowerInvariant() + "/PROFILE" })
        {
            using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", new() { ["email"] = user.Email, ["next"] = hostile });
            await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain(hostile).And.DoesNotContain("Back to account");
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(OidcHarness oidc, string path, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        return await oidc.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAsync(OidcHarness oidc, string path, string session, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        request.Content = new FormUrlEncodedContent(form);
        return await oidc.Client.SendAsync(request);
    }

    [GeneratedRegex("<dt>Setup key</dt><dd><code>([A-Z2-7]+)</code>")]
    private static partial Regex SetupKeyPattern();
}
