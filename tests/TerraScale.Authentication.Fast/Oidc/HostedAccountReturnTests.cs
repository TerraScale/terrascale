using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class HostedAccountReturnTests(TerraBaseFixture fixture)
{
    private const string Account = "https://dash.terrascale.tech/profile?tab=security";
    private const string Reset = "/ui/terrascale/reset";
    private const string Password = "synthetic account return password";
    private static string Security => "/ui/terrascale/security?next=" + Uri.EscapeDataString(Account);

    [Test]
    public async Task Aged_session_reauthenticates_and_keeps_account_context_through_enrollment_errors_and_recovery_navigation()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync("terrascale", "security@example.test", Passwords.Hash(Password), CancellationToken.None))!;
        using var initial = await oidc.PostFormAsync("/ui/terrascale/sign-in/password", new() { ["email"] = user.Email, ["password"] = Password });
        var oldSession = OidcHarness.SessionCookie(initial)!;
        await Assert.That(oldSession).IsNotNull();
        using var page = await SendAsync(oidc, Security, oldSession);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var next = Field(await page.Content.ReadAsStringAsync(), "next");
        await Assert.That(next).IsEqualTo(Account);
        clock.UtcNow += TimeSpan.FromMinutes(16);
        using var aged = await SendAsync(oidc, "/ui/terrascale/security/totp", oldSession, new() { ["next"] = next });
        await Assert.That(aged.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var reauth = await aged.Content.ReadAsStringAsync();
        await Assert.That(Link(reauth, "Back to account")).IsEqualTo(Account);
        using var signIn = await oidc.Client.GetAsync(Link(reauth, "Sign in"));
        var form = new Dictionary<string, string>
        { ["email"] = user.Email, ["password"] = Password + "wrong", ["next"] = Field(await signIn.Content.ReadAsStringAsync(), "next") };
        await Assert.That(form["next"]).IsEqualTo(Security);
        using var wrong = await oidc.PostFormAsync("/ui/terrascale/sign-in/password", form);
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        form["next"] = Field(await wrong.Content.ReadAsStringAsync(), "next");
        form["password"] = Password;
        using var renewed = await oidc.PostFormAsync("/ui/terrascale/sign-in/password", form);
        await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var session = OidcHarness.SessionCookie(renewed)!;
        await Assert.That(session).IsNotNull();
        await Assert.That(session).IsNotEqualTo(oldSession);
        using var resumed = await SendAsync(oidc, Link(await renewed.Content.ReadAsStringAsync(), "Continue"), session);
        using var setup = await SendAsync(oidc, "/ui/terrascale/security/totp", session,
            new() { ["next"] = Field(await resumed.Content.ReadAsStringAsync(), "next") });
        var setupHtml = await setup.Content.ReadAsStringAsync();
        await Assert.That(setup.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Link(setupHtml, "Cancel")).IsEqualTo(Security);
        using var invalid = await SendAsync(oidc, "/ui/terrascale/security/totp/confirm", session,
            new() { ["next"] = Field(setupHtml, "next"), ["code"] = "invalid" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        await Assert.That(Link(invalidHtml, "Back to account")).IsEqualTo(Account);
        using var retry = await SendAsync(oidc, "/ui/terrascale/security/totp", session, new() { ["next"] = Field(invalidHtml, "next") });
        var retryHtml = await retry.Content.ReadAsStringAsync();
        var secret = Regex.Match(retryHtml, "Enter this key: <code>([A-Z2-7]+)</code>").Groups[1].Value;
        using var confirm = await SendAsync(oidc, "/ui/terrascale/security/totp/confirm", session, new()
        {
            ["next"] = Field(retryHtml, "next"),
            ["code"] = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.UtcNow.UtcDateTime),
        });
        await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var recovery = await confirm.Content.ReadAsStringAsync();
        await Assert.That(Regex.Count(recovery, "<li><code>[A-Za-z0-9]{24}</code></li>")).IsEqualTo(8);
        await Assert.That(Link(recovery, "I saved them")).IsEqualTo(Security);
        await Assert.That(Link(recovery, "Back to account")).IsEqualTo(Account);
        using var saved = await SendAsync(oidc, Link(recovery, "I saved them"), OidcHarness.SessionCookie(confirm)!);
        var savedHtml = await saved.Content.ReadAsStringAsync();
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(savedHtml).Contains("On (authenticator app)");
        await Assert.That(Link(savedHtml, "Back to account")).IsEqualTo(Account);
        using var stale = await SendAsync(oidc, Security, oldSession);
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Found);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Existing_project_rejects_platform_account_return_even_after_successful_sign_in(bool nested)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("owner@example.test");
        var project = await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token), "Isolated project");
        const string Email = "end-user@example.test";
        await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, Email, Passwords.Hash(Password), CancellationToken.None);
        var next = nested ? Security : Account;
        foreach (var path in new[] { "sign-in", "reset" })
        {
            using var page = await oidc.Client.GetAsync($"/ui/{project}/{path}?next=" + Uri.EscapeDataString(next));
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await page.Content.ReadAsStringAsync();
            await Assert.That(html).DoesNotContain("Back to account");
            await Assert.That(html).DoesNotContain("dash.terrascale.tech");
        }
        using var signedIn = await oidc.PostFormAsync($"/ui/{project}/sign-in/password", new()
        { ["email"] = Email, ["password"] = Password, ["next"] = next });
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await signedIn.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("You are signed in.");
        await Assert.That(body).DoesNotContain("dash.terrascale.tech");
        await Assert.That(body).DoesNotContain("Back to account");
        await Assert.That(signedIn.Headers.GetValues("Set-Cookie").Any(cookie => cookie.StartsWith($"tsauth_session_{project}=", StringComparison.Ordinal))).IsTrue();
        await Assert.That(OidcHarness.SessionCookie(signedIn)).IsNull();
    }

    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"" + name + "\" value=\"([^\"]+)\"").Groups[1].Value);

    private static string Link(string html, string text) => WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]+)\">(?:<svg[^>]*>.*?</svg>)?" + text).Groups[1].Value);

    private static async Task<HttpResponseMessage> SendAsync(OidcHarness oidc, string path, string session, Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("Cookie", "tsauth_session=" + session);
        if (form is not null)
        {
            request.Headers.Add("Origin", OidcHarness.PublicOrigin);
            request.Content = new FormUrlEncodedContent(form);
        }
        return await oidc.Client.SendAsync(request);
    }

    [Test]
    public async Task Reset_preserves_account_return_on_errors_and_requires_a_new_sign_in()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync();
        clock.UtcNow += HostedAuth.MailCooldown;
        using var start = await oidc.PostFormAsync(Reset, new() { ["email"] = user.Email, ["next"] = Account });
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await start.Content.ReadAsStringAsync()).Contains("Back to account");
        var (_, mail) = await oidc.Email.NextAsync();
        var form = new Dictionary<string, string>
        {
            ["email"] = user.Email,
            ["next"] = Account,
            ["code"] = Regex.Match(mail.Text, @"\b[0-9]{6}\b").Value,
            ["password"] = "a new synthetic password",
        };
        using var invalid = await oidc.PostFormAsync(Reset + "/complete", new(form) { ["password"] = "short" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await invalid.Content.ReadAsStringAsync()).Contains("Back to account");
        using var complete = await oidc.PostFormAsync(Reset + "/complete", form);
        await Assert.That(complete.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await complete.Content.ReadAsStringAsync()).Contains("sign-in?next=" + Uri.EscapeDataString(Account));
        await Assert.That(OidcHarness.SessionCookie(complete)).IsNull();
        using var revoked = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(await OidcHarness.ErrorAsync(revoked)).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task Security_reauthentication_preserves_only_the_exact_platform_account_return()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var security = "/ui/terrascale/security?next=" + Uri.EscapeDataString(Account);
        using var page = await oidc.Client.GetAsync(security);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(page.Headers.Location!.OriginalString).IsEqualTo("/ui/terrascale/sign-in?next=" + Uri.EscapeDataString(security));
        using var signIn = await oidc.Client.GetAsync(page.Headers.Location);
        await Assert.That(await signIn.Content.ReadAsStringAsync()).Contains("Back to account");
    }

    [Test]
    [Arguments("https://dash.terrascale.tech.evil.test/profile?tab=security")]
    [Arguments("https://dash.terrascale.tech@evil.test/profile?tab=security")]
    [Arguments("//dash.terrascale.tech/profile?tab=security")]
    [Arguments("https://dash.terrascale.tech/profile?tab=security&next=https://evil.test")]
    [Arguments("https://dash.terrascale.tech/profile?tab=security#fragment")]
    [Arguments("https%3A%2F%2Fdash.terrascale.tech%2Fprofile%3Ftab%3Dsecurity")]
    public async Task Account_return_refuses_redirect_variants(string next)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        using var page = await oidc.Client.GetAsync(Reset + "?next=" + Uri.EscapeDataString(next));
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await page.Content.ReadAsStringAsync()).DoesNotContain("Back to account");
    }
}
