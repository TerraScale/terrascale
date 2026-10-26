using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Auth;

[ClassDataSource<DashboardRootFixture>]
public sealed class AuthProviderJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task SignedOidcAuthenticationUsesTheRealProviderContracts()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        using (var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(signedIn.StatusCode == HttpStatusCode.SeeOther && signedIn.Headers.Location?.OriginalString == "/api/v1/auth/me",
                "ts-auth's hosted sign-in completes through the dashboard callback");
        var user = await User(browser);
        Check(user.GetProperty("userId").GetString() == (await Observations(controls)).GetProperty("subject").GetString(),
            "The dashboard persists the signed provider subject");

        using var replicaBrowser = scenario.CreateClient(replica: true);
        var callback = await Authorize(replicaBrowser, controls);
        using (var exchanged = await replicaBrowser.GetAsync(callback))
            Check(exchanged.StatusCode == HttpStatusCode.SeeOther && exchanged.Headers.Location?.OriginalString == "/api/v1/auth/me",
                "The signed authorization-code response completes sign-in on another replica");
        Check((await User(replicaBrowser)).GetProperty("userId").GetString() == user.GetProperty("userId").GetString(),
            "The ID token and userinfo bind the same subject");
        using (var replay = await replicaBrowser.GetAsync(callback))
            Check(replay.StatusCode == HttpStatusCode.BadRequest, "The callback's persistent state is single use");

        var observed = await Observations(controls);
        Check(observed.GetProperty("codeExchanges").GetInt32() == 2, "Only the two expected authorization-code exchanges were made");
        CheckProtocol(observed);
    }

    [Test]
    public async Task InvalidSignedIdentityAndCallbackStateCannotCreateASession()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        foreach (var fault in new[] { "wrong-issuer", "wrong-audience", "bad-signature", "expired-id", "wrong-nonce", "unverified-id", "unverified-userinfo", "id-substitution", "userinfo-substitution" })
        {
            await Control(controls, new { command = "fault", value = fault });
            using var browser = scenario.CreateClient();
            var callback = await Authorize(browser, controls);
            using (var denied = await browser.GetAsync(callback))
            {
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Invalid signed/provider identity must be rejected: " + fault);
                Check(!denied.Headers.TryGetValues("Set-Cookie", out var cookies) || cookies.All(value => !value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)),
                    "Rejected provider identity cannot issue a dashboard session: " + fault);
            }
            using (var session = await browser.GetAsync("/api/v1/auth/me"))
                Check(session.StatusCode == HttpStatusCode.Unauthorized, "Rejected identity remains anonymous: " + fault);
            using (var replay = await browser.GetAsync(callback))
                Check(replay.StatusCode == HttpStatusCode.BadRequest, "Rejected callback state cannot be replayed: " + fault);
        }
        await Control(controls, new { command = "fault", value = "" });
        using var stateBrowser = scenario.CreateClient();
        var legitimateCallback = await Authorize(stateBrowser, controls);
        var before = (await Observations(controls)).GetProperty("codeExchanges").GetInt32();
        var invalidCallback = legitimateCallback.AbsoluteUri.Replace("state=", "state=foreign-", StringComparison.Ordinal);
        using (var wrongState = await stateBrowser.GetAsync(invalidCallback))
            Check(wrongState.StatusCode == HttpStatusCode.BadRequest, "Foreign callback state is refused before code redemption");
        using (var alreadyConsumed = await stateBrowser.GetAsync(legitimateCallback))
            Check(alreadyConsumed.StatusCode == HttpStatusCode.BadRequest, "A failed state check consumes the original flow");
        Check((await Observations(controls)).GetProperty("codeExchanges").GetInt32() == before,
            "Neither foreign nor consumed callback reaches the token producer");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    public async Task TwoRunningReplicasRotateOnceAndRereadProviderRevocation()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        using var second = scenario.CreateClient(replica: true);
        await Control(controls, new { command = "short-grants" });
        using var signedIn = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "Short-lived real provider grant creates a native browser session");
        var cookie = SessionCookie(signedIn);
        await Control(controls, new { command = "advance", seconds = 2 });
        await Control(controls, new { command = "pause-refresh" });
        var firstRequest = first.GetAsync("/api/v1/auth/me");
        await WaitForRefresh(controls);
        var replicaRequest = Request(second, "/api/v1/auth/me", cookie);
        await Task.Delay(150);
        Check(!replicaRequest.IsCompleted && (await Observations(controls)).GetProperty("refreshes").GetInt32() == 1,
            "A second running replica waits behind the persistent refresh lease");
        await Control(controls, new { command = "release-refresh" });
        using var refreshed = await firstRequest;
        using var replicated = await replicaRequest;
        Check(refreshed.StatusCode == HttpStatusCode.OK && replicated.StatusCode == HttpStatusCode.OK,
            "Both native requests resume after one successful provider rotation");
        var firstUser = await Json(refreshed);
        var secondUser = await Json(replicated);
        Check(firstUser.GetProperty("userId").GetString() == secondUser.GetProperty("userId").GetString() &&
            firstUser.GetProperty("accountId").GetString() == secondUser.GetProperty("accountId").GetString(),
            "Both replicas use the refreshed persistent credential for the same account");
        using (var next = await Request(second, "/api/v1/auth/me", cookie))
            Check(next.StatusCode == HttpStatusCode.OK, "The rotated credential is available for the next replica request");
        Check((await Observations(controls)).GetProperty("refreshes").GetInt32() == 1, "A completed rotation is never replayed");
        await Control(controls, new { command = "revoke" });
        using (var revoked = await Request(second, "/api/v1/auth/me", cookie))
            Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "A fresh native request rereads authoritative provider revocation");
        using (var admin = await first.GetAsync("/admin/users"))
            Check(admin.StatusCode == HttpStatusCode.SeeOther, "A revoked grant cannot reach even denied administration surfaces");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    public async Task AccessExpiryDuringIssuerValidationRefreshesTheSameBrowserSession()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        await Control(controls, new { command = "short-grants" });
        using var login = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
        Check(login.StatusCode == HttpStatusCode.SeeOther, "A short-lived access grant starts a persistent browser session");
        var cookie = SessionCookie(login);
        await Control(controls, new { command = "pause-next-userinfo" });
        var pending = first.GetAsync("/api/v1/auth/me");
        await scenario.WaitForUserInfoAsync();
        await Control(controls, new { command = "advance", seconds = 2 });
        await Control(controls, new { command = "release-userinfo" });
        using var recovered = await pending;
        Check(recovered.StatusCode == HttpStatusCode.OK,
            "Access expiry during issuer validation refreshes rather than deletes the still-valid browser session; actual " + (int)recovered.StatusCode);
        using var next = await Request(replica, "/api/v1/auth/me", cookie);
        Check(next.StatusCode == HttpStatusCode.OK, "The original cookie still resolves on another replica after boundary refresh");
        Check((await Observations(controls)).GetProperty("refreshes").GetInt32() == 1, "The boundary recovery consumes exactly one rotating secret");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IssuerRevocationDuringValidationStillEndsTheBrowserSession(bool alsoExpire)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        await Control(controls, new { command = "short-grants" });
        using var login = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
        Check(login.StatusCode == HttpStatusCode.SeeOther, "The revoked session begins with a valid provider grant");
        var cookie = SessionCookie(login);
        await Control(controls, new { command = "pause-next-userinfo" });
        var pending = first.GetAsync("/api/v1/auth/me");
        await scenario.WaitForUserInfoAsync();
        if (alsoExpire) await Control(controls, new { command = "advance", seconds = 2 });
        await Control(controls, new { command = "revoke" });
        await Control(controls, new { command = "release-userinfo" });
        using var rejected = await pending;
        Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "Issuer revocation cannot recover an authenticated request, including at access expiry");
        using var next = await Request(replica, "/api/v1/auth/me", cookie);
        Check(next.StatusCode == HttpStatusCode.Unauthorized, "A captured revoked cookie stays rejected on another replica");
        Check((await Observations(controls)).GetProperty("refreshes").GetInt32() == (alsoExpire ? 1 : 0),
            "Only access expiry attempts refresh, and the issuer rejects a revoked rotating grant");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    public async Task SessionSurvivesARefreshTsAuthCouldNotStart()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        await Control(controls, new { command = "short-grants" });
        using var signedIn = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "The browser obtains a short-lived signed provider grant");
        await Control(controls, new { command = "advance", seconds = 2 });
        await Control(controls, new { command = "unavailable-refresh" });
        using (var unavailable = await first.GetAsync("/api/v1/auth/me"))
            Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "A restarting ts-auth makes the request unavailable");
        await Control(controls, new { command = "advance", seconds = 61 });
        using (var recovered = await first.GetAsync("/api/v1/auth/me"))
            Check(recovered.StatusCode == HttpStatusCode.OK, "The same session refreshes once ts-auth is back");
        Check((await Observations(controls)).GetProperty("refreshes").GetInt32() == 1, "The unread secret is rotated exactly once");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    public async Task LostRefreshResponseIsNeverReplayedByAnotherReplica()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        using var second = scenario.CreateClient(replica: true);
        await Control(controls, new { command = "short-grants" });
        using var signedIn = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "The browser obtains a short-lived signed provider grant");
        var cookie = SessionCookie(signedIn);
        await Control(controls, new { command = "advance", seconds = 2 });
        await Control(controls, new { command = "drop-refresh-response" });
        using (var uncertain = await first.GetAsync("/api/v1/auth/me"))
            Check(uncertain.StatusCode == HttpStatusCode.ServiceUnavailable, "A connection lost after producer rotation returns an unavailable result");
        Check((await Observations(controls)).GetProperty("refreshes").GetInt32() == 1, "The producer consumed the old refresh secret once");
        await Control(controls, new { command = "advance", seconds = 61 });
        using (var expiredLease = await Request(second, "/api/v1/auth/me", cookie))
            Check(expiredLease.StatusCode == HttpStatusCode.Unauthorized, "An uncertain expired lease requires the browser to sign in again");
        using (var stillEnded = await first.GetAsync("/api/v1/auth/me"))
            Check(stillEnded.StatusCode == HttpStatusCode.Unauthorized, "The expired uncertain session stays ended on the original replica");
        Check((await Observations(controls)).GetProperty("refreshes").GetInt32() == 1, "Another replica never retries the consumed rotating secret");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    public async Task HostedPasswordChangeRevokesAllSessionsDuringAnInflightRefresh()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        using var second = scenario.CreateClient(replica: true);
        await Control(controls, new { command = "short-grants" });
        using var firstLogin = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
        Check(firstLogin.StatusCode == HttpStatusCode.SeeOther, "First browser is signed in using the real producer");
        var firstCookie = SessionCookie(firstLogin);
        await Control(controls, new { command = "advance", seconds = 2 });
        await Control(controls, new { command = "pause-refresh" });
        var lateRefresh = first.GetAsync("/api/v1/auth/me");
        await WaitForRefresh(controls);
        using var secondLogin = await HttpJourney.LoginAsync(second, returnTo: "/api/v1/auth/me");
        Check(secondLogin.StatusCode == HttpStatusCode.SeeOther, "A separate browser receives a fresh hosted sign-in grant");
        var secondCookie = SessionCookie(secondLogin);
        const string newPassword = "new-provider-password-123";
        await Control(controls, new { command = "change-password", value = newPassword });
        await Control(controls, new { command = "release-refresh" });
        using (var late = await lateRefresh)
            Check(late.StatusCode == HttpStatusCode.Unauthorized, "A previously produced but now revoked refresh answer cannot finish authentication");
        using (var oldFirst = await Request(first, "/api/v1/auth/me", firstCookie))
        using (var oldSecond = await Request(second, "/api/v1/auth/me", secondCookie))
            Check(oldFirst.StatusCode == HttpStatusCode.Unauthorized && oldSecond.StatusCode == HttpStatusCode.Unauthorized,
                "A password change on ts-auth wins over the leased refresh and invalidates both captured browser cookies");
        using (var oldPassword = await HttpJourney.LoginAsync(second, returnTo: "/api/v1/auth/me"))
            Check(oldPassword.StatusCode == HttpStatusCode.Unauthorized, "The previous password no longer obtains a provider grant");
        using (var newLogin = await HttpJourney.LoginAsync(second, password: newPassword, returnTo: "/api/v1/auth/me"))
            Check(newLogin.StatusCode == HttpStatusCode.SeeOther, "The new password obtains a fresh provider grant after all sessions ended");
        _ = await User(second);
        var observed = await Observations(controls);
        Check(observed.GetProperty("managementRequests").GetInt32() == 0, "The dashboard calls no unsupported management contract");
        Check(observed.GetProperty("refreshes").GetInt32() == 1, "Password change never retries the leased refresh secret");
        CheckProtocol(observed);
    }

    [Test]
    public async Task ProviderOutagesAndForgedPersistedAdministratorClaimsFailClosed()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        await Control(controls, new { command = "fault", value = "untrusted-endpoint" });
        using (var untrusted = await browser.GetAsync("/login?returnTo=%2Fapi%2Fv1%2Fauth%2Fme"))
            Check(untrusted.StatusCode == HttpStatusCode.ServiceUnavailable && untrusted.Headers.Location is null,
                "A discovery document naming another host is never followed");
        await Control(controls, new { command = "fault", value = "invalid-client" });
        using (var invalidClient = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(invalidClient.StatusCode == HttpStatusCode.ServiceUnavailable, "Confidential client failure is an unavailable dependency rather than anonymous authorization");
        await Control(controls, new { command = "fault", value = "upstream-rate-limit" });
        using (var throttled = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(throttled.StatusCode == HttpStatusCode.TooManyRequests && throttled.Headers.Location is null && throttled.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60),
                "A rate-limited code exchange passes ts-auth's 429 and Retry-After through with a manual retry, never an automatic redirect back into ts-auth: " + (int)throttled.StatusCode);
        await Control(controls, new { command = "fault", value = "" });
        using (var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "A recovered producer admits the hosted sign-in");
        await Control(controls, new { command = "spoof-stored-admin" });
        using (var admin = await browser.GetAsync("/admin/users"))
            Check(admin.StatusCode == HttpStatusCode.Forbidden && (await admin.Content.ReadAsStringAsync()).Contains("Your account does not have platform admin access.", StringComparison.Ordinal),
                "Stored dashboard fields cannot manufacture a platform administrator claim");
        Check((await Observations(controls)).GetProperty("storedClaimSpoof").GetBoolean(), "The fixture actually injected the untrusted persisted claims");
        await Control(controls, new { command = "fault", value = "userinfo-outage" });
        using (var outage = await browser.GetAsync("/admin/users"))
            Check(outage.StatusCode == HttpStatusCode.ServiceUnavailable, "Admin denial cannot bypass unavailable authoritative token validation");
        await Control(controls, new { command = "fault", value = "" });
        using (var recovered = await browser.GetAsync("/admin/users"))
            Check(recovered.StatusCode == HttpStatusCode.Forbidden, "Recovery restores the same ordinary-user denial");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    [Arguments("userinfo-substitution")]
    [Arguments("userinfo-email-substitution")]
    [Arguments("unverified-userinfo")]
    [Arguments("expire-access")]
    [Arguments("revoke")]
    public async Task EveryNativeSessionReadRequiresCurrentOwningUserinfo(string failure)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "An active signed access token creates a native session");
        var cookie = SessionCookie(signedIn);
        _ = await User(browser);
        var before = (await Observations(controls)).GetProperty("userinfoRequests").GetInt32();
        await Control(controls, failure is "expire-access" or "revoke" ? new { command = failure, value = "" } : new { command = "fault", value = failure });
        using (var denied = await browser.GetAsync("/api/v1/auth/me"))
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Current provider activity and exact verified identity are required: " + failure);
        await Control(controls, new { command = "fault", value = "" });
        using (var otherReplica = await Request(replica, "/api/v1/auth/me", cookie))
            Check(otherReplica.StatusCode == HttpStatusCode.Unauthorized, "An inactive or substituted grant is removed persistently across replicas: " + failure);
        var observed = await Observations(controls);
        Check(observed.GetProperty("userinfoRequests").GetInt32() == before + 1 && observed.GetProperty("refreshes").GetInt32() == 0,
            "An unexpired dashboard grant is checked with userinfo, and its removed session does not trigger a replay");
        CheckProtocol(observed);
    }

    [Test]
    public async Task AnUnavailableUserinfoPreservesTheSessionWithoutGrantingAccess()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "The provider initially admits the browser");
        var cookie = SessionCookie(signedIn);
        await Control(controls, new { command = "fault", value = "userinfo-outage" });
        using (var unavailable = await browser.GetAsync("/api/v1/auth/me"))
            Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "Unavailable current identity is a dependency failure");
        using (var unavailableReplica = await Request(replica, "/api/v1/auth/me", cookie))
            Check(unavailableReplica.StatusCode == HttpStatusCode.ServiceUnavailable, "The replica also fails closed while userinfo is unavailable");
        await Control(controls, new { command = "fault", value = "" });
        _ = await User(browser);
        using (var recovered = await Request(replica, "/api/v1/auth/me", cookie))
            Check(recovered.StatusCode == HttpStatusCode.OK, "A recovered producer validates the same persisted session on another replica");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    [Arguments("access-wrong-issuer")]
    [Arguments("access-wrong-audience")]
    [Arguments("access-bad-signature")]
    [Arguments("access-identity-token")]
    [Arguments("access-expired")]
    public async Task UserinfoRejectsAnInvalidAccessTokenDespiteAValidIdToken(string fault)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        await Control(controls, new { command = "fault", value = fault });
        using (var rejected = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
        {
            Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "The owning issuer rejects the invalid access token: " + fault);
            Check(!rejected.Headers.TryGetValues("Set-Cookie", out var cookies) || cookies.All(value => !value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)),
                "A valid ID token cannot substitute for a signed active access token: " + fault);
        }
        using (var anonymous = await browser.GetAsync("/api/v1/auth/me"))
            Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Rejected access-token evidence creates no dashboard session");
        var observed = await Observations(controls);
        Check(observed.GetProperty("codeExchanges").GetInt32() == 1 && observed.GetProperty("userinfoRequests").GetInt32() == 1,
            "The code exchange reaches actual signed access-token validation after independently valid ID-token checks");
        CheckProtocol(observed);
    }

    [Test]
    public async Task HostedPasswordChangeInvalidatesBrowserAndPatFamiliesAcrossReplicas()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "The browser owns an active provider family");
        var cookie = SessionCookie(signedIn);
        var pat = await HttpJourney.CreatePatAsync(browser, "Independent password-change family");
        await ActivePat(replica, pat);
        const string replacement = "code-confirmed-password-123";
        await Control(controls, new { command = "change-password", value = replacement });
        using (var ended = await Request(replica, "/api/v1/auth/me", cookie))
            Check(ended.StatusCode == HttpStatusCode.Unauthorized, "The previous browser is refused on another replica");
        using var patRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        patRequest.Headers.Authorization = new("Bearer", pat);
        using var endedPat = await replica.SendAsync(patRequest);
        Check(endedPat.StatusCode == HttpStatusCode.Unauthorized, "The independent PAT family lost owning issuer authority");
        using var credentials = scenario.CreateClient();
        using var oldPassword = await HttpJourney.LoginAsync(credentials, returnTo: "/api/v1/auth/me");
        Check(oldPassword.StatusCode == HttpStatusCode.Unauthorized, "The hosted password change replaced the original password");
        using var newPassword = await HttpJourney.LoginAsync(credentials, password: replacement, returnTo: "/api/v1/auth/me");
        Check(newPassword.StatusCode == HttpStatusCode.SeeOther, "The confirmed new password obtains a grant");
        var observed = await Observations(controls);
        Check(observed.GetProperty("managementRequests").GetInt32() == 0, "The dashboard uses no invented PATCH or revoke-all contract");
        CheckProtocol(observed);
    }

    [Test]
    public async Task RefreshUserinfoCannotSubstituteTheOriginalProviderSubject()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        await Control(controls, new { command = "short-grants" });
        using var signedIn = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "The original subject starts a native short-lived session");
        var cookie = SessionCookie(signedIn);
        await Control(controls, new { command = "advance", seconds = 2 });
        await Control(controls, new { command = "fault", value = "userinfo-substitution" });
        using (var substituted = await first.GetAsync("/api/v1/auth/me"))
            Check(substituted.StatusCode == HttpStatusCode.Unauthorized, "A refresh answer without an ID token must still prove the original userinfo subject");
        await Control(controls, new { command = "fault", value = "" });
        using (var ended = await Request(replica, "/api/v1/auth/me", cookie))
            Check(ended.StatusCode == HttpStatusCode.Unauthorized, "Identity substitution removes the persistent session for every replica");
        var observed = await Observations(controls);
        Check(observed.GetProperty("refreshes").GetInt32() == 1, "A rejected identity cannot replay its rotating refresh secret");
        CheckProtocol(observed);
    }

    private static async Task ActivePat(HttpClient replica, string pat)
    {
        var response = await HttpJourney.McpAsync(replica, pat, "tools/call", new { name = "get_profile", arguments = new { } });
        var result = response.GetProperty("result");
        Check(!result.TryGetProperty("isError", out var error) || !error.GetBoolean(), "The independent native-issued PAT still has actual remote authority");
        Check(result.GetProperty("structuredContent").GetProperty("email").GetString() == "user@example.com",
            "The preserved PAT resolves its own owning grant and profile across replicas");
    }

    private static async Task<Uri> Authorize(HttpClient browser, HttpClient producer)
    {
        using var started = await browser.GetAsync("/login?returnTo=%2Fapi%2Fv1%2Fauth%2Fme");
        Check(started.StatusCode == HttpStatusCode.Redirect && started.Headers.Location is { Scheme: "https", Host: "auth.terrascale.tech", AbsolutePath: "/terrascale/oauth/authorize" },
            "Sign-in redirects to the discovered trusted authorization endpoint");
        var target = started.Headers.Location!;
        // ts-auth answers login_required to prompt=login, so the dashboard never sends a prompt.
        Check(!target.Query.Contains("prompt", StringComparison.Ordinal), "Sign-in sends no prompt ts-auth refuses");
        using var request = new HttpRequestMessage(HttpMethod.Get, target.PathAndQuery);
        request.Headers.Host = target.Authority;
        using var authorized = await producer.SendAsync(request);
        Check(authorized.StatusCode == HttpStatusCode.Redirect && authorized.Headers.Location?.AbsolutePath == "/login/callback",
            "The external producer validates nonce, state, PKCE, offline scope, and original redirect URI");
        return authorized.Headers.Location!;
    }

    private static async Task<JsonElement> User(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/me");
        Check(response.StatusCode == HttpStatusCode.OK, "The native identity API confirms the authenticated session");
        return await Json(response);
    }

    private static async Task Control(HttpClient producer, object command)
    {
        using var response = await producer.PostAsJsonAsync("/__test/control", command);
        Check(response.StatusCode == HttpStatusCode.OK, "The external test producer accepted scenario setup");
    }

    private static async Task<JsonElement> Observations(HttpClient producer)
    {
        using var response = await producer.GetAsync("/__test/observations");
        Check(response.StatusCode == HttpStatusCode.OK, "The external test producer reports its observed network requests");
        return await Json(response);
    }

    private static async Task WaitForRefresh(HttpClient producer)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await Observations(producer)).GetProperty("refreshes").GetInt32() == 1) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("The running dashboard did not reach the external refresh-token producer.");
    }

    private static void CheckProtocol(JsonElement observed)
    {
        Check(observed.GetProperty("contractFailures").GetInt32() == 0, "Every producer request preserves its documented origin, client credentials, subject and form/JSON binding");
        Check(observed.GetProperty("credentialsEncrypted").GetBoolean(), "No actual access or refresh secret appears in persisted plaintext dashboard documents");
        Check(observed.GetProperty("unsupportedIntrospectionRequests").GetInt32() == 0,
            "Dashboard authentication never calls the owning producer's unsupported introspection endpoint");
    }

    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];

    private static Task<HttpResponseMessage> Request(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
