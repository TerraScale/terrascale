using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
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
        public async Task CallbackCreatesTheSessionAndALaterLoginDoesNotLoop()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var browser = scenario.CreateClient();
            using (var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            {
                await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(signedIn.Headers.Location!.OriginalString).IsEqualTo("/api/v1/auth/me");
            }
            using (var me = await browser.GetAsync("/api/v1/auth/me"))
            {
                await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
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
            using (var start = await browser.GetAsync("/login"))
            {
                await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            }

            using (var forged = await browser.GetAsync("/login/callback?state=" + new string('a', 64) + "&code=authorization-forged"))
            {
                await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                await Assert.That(forged.Headers.Location).IsNull();
            }
            using (var me = await browser.GetAsync("/api/v1/auth/me"))
            {
                await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            using var denied = scenario.CreateClient();
            using var started = await denied.GetAsync("/login");
            var state = QueryHelpers.ParseQuery(started.Headers.Location!.Query)["state"].ToString();
            using (var refused = await denied.GetAsync("/login/callback?error=access_denied&state=" + state))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("href=\"/login\"");
            }
            using (var me = await denied.GetAsync("/api/v1/auth/me"))
            {
                await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
        }

        [Test]
        public async Task AccessExpiryDuringIssuerValidationRefreshesTheSameBrowserSession()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "short-grants" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var cookie = Cookie(login);

            // The access grant expires while the issuer is still validating it: the
            // persistent session must refresh rather than sign the browser out.
            await Control(producer, new { command = "pause-next-userinfo" });
            var pending = browser.GetAsync("/api/v1/auth/me");
            await scenario.WaitForUserInfoAsync();
            await Control(producer, new { command = "advance", seconds = 2 });
            await Control(producer, new { command = "release-userinfo" });
            using var recovered = await pending;
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);

            using var next = await Request(replica, "/api/v1/auth/me", cookie);
            await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await Observe(producer)).GetProperty("refreshes").GetInt32()).IsEqualTo(1);
        }

        [Test]
        public async Task IssuerRevocationDuringValidationStillEndsTheBrowserSession()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "short-grants" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var cookie = Cookie(login);

            // Revoked authority cannot recover through the boundary refresh, not even at expiry.
            await Control(producer, new { command = "pause-next-userinfo" });
            var pending = browser.GetAsync("/api/v1/auth/me");
            await scenario.WaitForUserInfoAsync();
            await Control(producer, new { command = "advance", seconds = 2 });
            await Control(producer, new { command = "revoke" });
            await Control(producer, new { command = "release-userinfo" });
            using var rejected = await pending;
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

            using var next = await Request(replica, "/api/v1/auth/me", cookie);
            await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task LogoutEndsTheSessionAtHostedEndSession()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var browser = scenario.CreateClient();
            using (var signedIn = await HttpJourney.LoginAsync(browser))
            {
                await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var csrf = await HttpJourney.CsrfAsync(browser);
            using var logout = await HttpJourney.PostFormAsync(browser, "/api/v1/auth/logout", new() { ["_csrf"] = csrf });
            await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var location = logout.Headers.Location!;
            await Assert.That(location.GetLeftPart(UriPartial.Path)).IsEqualTo("https://auth.terrascale.tech/terrascale/oauth/logout");
            var query = QueryHelpers.ParseQuery(location.Query);
            await Assert.That(query["id_token_hint"].ToString().Split('.').Length).IsEqualTo(3);
            await Assert.That(query["post_logout_redirect_uri"].ToString()).IsEqualTo(new Uri(scenario.Origin, "/login").AbsoluteUri);
            await Assert.That(query["client_id"].ToString().Length).IsGreaterThan(0);
            using (var me = await browser.GetAsync("/api/v1/auth/me"))
            {
                await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
            // ts-auth's hosted "Signed out" page continues to /login, which starts a fresh authorization rather than a session.
            using var after = await browser.GetAsync("/login");
            await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(after.Headers.Location!.Host).IsEqualTo("auth.terrascale.tech");
        }

        private static async Task Control(HttpClient producer, object command)
        {
            using var response = await producer.PostAsJsonAsync("/__test/control", command);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        private static async Task<JsonElement> Observe(HttpClient producer)
        {
            using var response = await producer.GetAsync("/__test/observations");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        private static string Cookie(HttpResponseMessage response)
        {
            return response.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];
        }

        private static Task<HttpResponseMessage> Request(HttpClient client, string path, string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }
    }
}
