using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Auth;

// The dashboard as a plain OIDC client of ts-auth's hosted pages, through the real
// TsAuthGateway against the test-only ts-auth producer.
[ClassDataSource<DashboardRootFixture>]
public sealed class HostedSignInJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task LoginRedirectsToHostedAuthorizeWithPkceStateAndNonce()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using var start = await browser.GetAsync("/login?returnTo=%2Fdatabases");
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = start.Headers.Location!;
        await Assert.That(location.GetLeftPart(UriPartial.Path)).IsEqualTo("https://auth.terrascale.tech/terrascale/oauth/authorize");
        var query = QueryHelpers.ParseQuery(location.Query);
        await Assert.That(query["response_type"].ToString()).IsEqualTo("code");
        await Assert.That(query["code_challenge_method"].ToString()).IsEqualTo("S256");
        await Assert.That(query["code_challenge"].ToString().Length).IsEqualTo(43);
        await Assert.That(query["state"].ToString().Length).IsEqualTo(64);
        await Assert.That(query["nonce"].ToString().Length).IsEqualTo(64);
        await Assert.That(query["scope"].ToString().Split(' ')).Contains("offline_access");
        await Assert.That(query["redirect_uri"].ToString()).IsEqualTo(new Uri(scenario.Origin, "/login/callback").AbsoluteUri);
        // Only a random bearer for the encrypted, persisted flow travels in the browser.
        var cookie = start.Headers.GetValues("Set-Cookie").Last(value => value.StartsWith("ts-dashboard-oidc=", StringComparison.Ordinal));
        await Assert.That(cookie).Contains("httponly");
        await Assert.That(cookie).Contains("samesite=lax");
        await Assert.That(cookie).DoesNotContain(query["state"].ToString());
    }

    [Test]
    public async Task RegisterOpensHostedSignUpThatReturnsThroughAuthorize()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using var register = await browser.GetAsync("/register?plan=starter");
        await Assert.That(register.Headers.Location!.OriginalString).IsEqualTo("/onboarding?plan=starter");
        using var onboarding = await browser.GetAsync("/onboarding?plan=starter");
        await Assert.That(onboarding.Headers.Location!.OriginalString).IsEqualTo("/login?signup=1&returnTo=%2Fonboarding%3Fplan%3Dstarter");
        using var start = await browser.GetAsync(onboarding.Headers.Location!.OriginalString);
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = start.Headers.Location!;
        await Assert.That(location.GetLeftPart(UriPartial.Path)).IsEqualTo("https://auth.terrascale.tech/ui/terrascale/sign-up");
        var next = new Uri(new Uri("https://auth.terrascale.tech"), QueryHelpers.ParseQuery(location.Query)["next"].ToString());
        await Assert.That(next.GetLeftPart(UriPartial.Path)).IsEqualTo("https://auth.terrascale.tech/terrascale/oauth/authorize");
        await Assert.That(QueryHelpers.ParseQuery(next.Query)["state"].ToString().Length).IsEqualTo(64);
    }

    [Test]
    public async Task HeadLoginAlsoRedirectsToHostedAuthorize()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        // Uptime probes and `curl -I` send HEAD; they must see the same redirect as a browser.
        using var start = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/login"));
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(start.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo("https://auth.terrascale.tech/terrascale/oauth/authorize");
    }

    [Test]
    public async Task CallbackCreatesTheSessionAndALaterLoginDoesNotLoop()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using (var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
        {
            await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(signedIn.Headers.Location!.OriginalString).IsEqualTo("/api/v1/auth/me");
        }
        using (var me = await browser.GetAsync("/api/v1/auth/me")) await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // A second /login with a live session continues to returnTo instead of re-entering ts-auth.
        using var again = await browser.GetAsync("/login?returnTo=%2Fdatabases");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(again.Headers.Location!.OriginalString).IsEqualTo("/databases");
    }

    [Test]
    public async Task ForgedStateAndDeniedConsentNeverCreateASession()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using (var start = await browser.GetAsync("/login")) await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using (var forged = await browser.GetAsync("/login/callback?state=" + new string('a', 64) + "&code=authorization-forged"))
        {
            await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(forged.Headers.Location).IsNull();
        }
        using (var me = await browser.GetAsync("/api/v1/auth/me")) await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var denied = scenario.CreateClient();
        using var started = await denied.GetAsync("/login");
        var state = QueryHelpers.ParseQuery(started.Headers.Location!.Query)["state"].ToString();
        using (var refused = await denied.GetAsync("/login/callback?error=access_denied&state=" + state))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("href=\"/login\"");
        }
        using (var me = await denied.GetAsync("/api/v1/auth/me")) await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task LogoutEndsTheSessionAtHostedEndSession()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using (var signedIn = await HttpJourney.LoginAsync(browser)) await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(browser);
        using var logout = await HttpJourney.PostFormAsync(browser, "/api/v1/auth/logout", new() { ["_csrf"] = csrf });
        await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var location = logout.Headers.Location!;
        await Assert.That(location.GetLeftPart(UriPartial.Path)).IsEqualTo("https://auth.terrascale.tech/terrascale/oauth/logout");
        var query = QueryHelpers.ParseQuery(location.Query);
        await Assert.That(query["id_token_hint"].ToString().Split('.').Length).IsEqualTo(3);
        await Assert.That(query["post_logout_redirect_uri"].ToString()).IsEqualTo(new Uri(scenario.Origin, "/login").AbsoluteUri);
        await Assert.That(query["client_id"].ToString().Length).IsGreaterThan(0);
        using (var me = await browser.GetAsync("/api/v1/auth/me")) await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // ts-auth's hosted "Signed out" page continues to /login, which starts a fresh authorization rather than a session.
        using var after = await browser.GetAsync("/login");
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(after.Headers.Location!.Host).IsEqualTo("auth.terrascale.tech");
    }

    [Test]
    public async Task TheDashboardsOwnSignInRoutesAreGone()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        foreach (var path in new[] { "/login/code", "/login/sso", "/reset-password", "/cli/approve", "/oauth/consent", "/agent/approve" })
            using (var response = await browser.GetAsync(path))
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(path);
        foreach (var path in new[] { "/login/code/verify", "/login/code/resend", "/reset-password/complete", "/api/v1/dashboard/cli/device-authorization",
                     "/api/v1/dashboard/cli/token", "/cli/approve/submit", "/api/v1/dashboard/profile/password", "/onboarding", "/login" })
            using (var response = await HttpJourney.PostFormAsync(browser, path, new() { ["email"] = "user@example.com", ["password"] = "test-password" }))
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(path);
    }
}
