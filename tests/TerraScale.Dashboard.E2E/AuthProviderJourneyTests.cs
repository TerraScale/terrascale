using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardRootFixture>]
public sealed class AuthProviderJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task SignedOidcAndHeadlessAuthenticationUseTheRealProviderContracts()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var passwordBrowser = scenario.CreateClient();
        using (var signedIn = await HttpJourney.LoginAsync(passwordBrowser, returnTo: "/api/v1/auth/me"))
            Check(signedIn.StatusCode == HttpStatusCode.SeeOther && signedIn.Headers.Location?.OriginalString == "/api/v1/auth/me",
                "The actual headless password grant signs in through the native form");
        var passwordUser = await User(passwordBrowser);
        Check(passwordUser.GetProperty("userId").GetString() == (await Observations(controls)).GetProperty("subject").GetString(),
            "The dashboard persists the signed provider subject");

        using var codeBrowser = scenario.CreateClient();
        var codeCsrf = await HttpJourney.CsrfAsync(codeBrowser, "/login/code");
        using (var started = await HttpJourney.PostFormAsync(codeBrowser, "/login/code", new()
        { ["_csrf"] = codeCsrf, ["email"] = "user@example.com", ["redirectTo"] = "/api/v1/auth/me" }))
            Check(started.StatusCode == HttpStatusCode.OK, "Native email-code start reaches the authenticated JSON headless contract");
        using (var verified = await HttpJourney.PostFormAsync(codeBrowser, "/login/code/verify", new()
        { ["_csrf"] = codeCsrf, ["email"] = "user@example.com", ["code"] = "123456", ["redirectTo"] = "/api/v1/auth/me" }))
            Check(verified.StatusCode == HttpStatusCode.SeeOther, "Native email verification uses its separate real grant");
        Check((await User(codeBrowser)).GetProperty("userId").GetString() == passwordUser.GetProperty("userId").GetString(),
            "Both authenticated grant types resolve the same provider subject");

        using var ssoBrowser = scenario.CreateClient();
        var callback = await Authorize(ssoBrowser, controls);
        using (var exchanged = await ssoBrowser.GetAsync(callback))
            Check(exchanged.StatusCode == HttpStatusCode.SeeOther && exchanged.Headers.Location?.OriginalString == "/api/v1/auth/me",
                "The signed authorization-code response completes native SSO");
        Check((await User(ssoBrowser)).GetProperty("userId").GetString() == passwordUser.GetProperty("userId").GetString(),
            "SSO binds the ID token and userinfo to the same subject");
        using (var replay = await ssoBrowser.GetAsync(callback))
            Check(replay.StatusCode == HttpStatusCode.BadRequest, "The callback's persistent state is single use");

        var observed = await Observations(controls);
        Check(observed.GetProperty("passwordGrants").GetInt32() == 1 && observed.GetProperty("codeExchanges").GetInt32() == 1,
            "Only the expected real password and authorization-code exchanges were made");
        Check(observed.GetProperty("actions").EnumerateArray().Any(item => item.GetString() == "email-code/start"),
            "The owning producer received the code-email request");
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
    public async Task NativePasswordChangeRevokesAllSessionsDuringAnInflightRefresh()
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
        Check(secondLogin.StatusCode == HttpStatusCode.SeeOther, "A separate browser receives a fresh native password grant");
        var secondCookie = SessionCookie(secondLogin);
        var csrf = await HttpJourney.CsrfAsync(second, "/profile?tab=security");
        const string newPassword = "new-provider-password-123";
        using (var started = await StartPasswordForm(second, csrf))
            Check(started.StatusCode == HttpStatusCode.SeeOther && started.Headers.Location?.OriginalString == "/profile?tab=security&status=code-sent",
                "Current-password proof starts the owning producer reset while the older refresh is leased");
        using (var changed = await CompletePasswordForm(second, csrf, newPassword))
        {
            Check(changed.StatusCode == HttpStatusCode.SeeOther && changed.Headers.Location?.OriginalString == "/login",
                "Native signed-in password change completes and signs the current browser out");
            Check(changed.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("ts-dashboard-session=;", StringComparison.Ordinal)),
                "Password change clears the browser session cookie");
        }
        await Control(controls, new { command = "release-refresh" });
        using (var late = await lateRefresh)
            Check(late.StatusCode == HttpStatusCode.Unauthorized, "A previously produced but now revoked refresh answer cannot finish authentication");
        using (var oldFirst = await Request(first, "/api/v1/auth/me", firstCookie))
        using (var oldSecond = await Request(second, "/api/v1/auth/me", secondCookie))
            Check(oldFirst.StatusCode == HttpStatusCode.Unauthorized && oldSecond.StatusCode == HttpStatusCode.Unauthorized,
                "Confirmed password reset wins over the leased refresh and invalidates both captured browser cookies");
        using (var oldPassword = await HttpJourney.LoginAsync(second, returnTo: "/api/v1/auth/me"))
            Check(oldPassword.StatusCode == HttpStatusCode.Unauthorized, "The previous password no longer obtains a provider grant");
        using (var newLogin = await HttpJourney.LoginAsync(second, password: newPassword, returnTo: "/api/v1/auth/me"))
            Check(newLogin.StatusCode == HttpStatusCode.SeeOther, "The new password obtains a fresh provider grant after all sessions ended");
        _ = await User(second);
        var observed = await Observations(controls);
        var resetActions = observed.GetProperty("actions").EnumerateArray().Select(item => item.GetString()).Where(item => item?.StartsWith("password-reset/", StringComparison.Ordinal) == true).ToArray();
        Check(resetActions.SequenceEqual(new[] { "password-reset/start", "password-reset/complete" }) &&
            observed.GetProperty("managementRequests").GetInt32() == 0 && observed.GetProperty("managementMutations").GetInt32() == 0,
            "One admitted reset completes once, revokes the provider families, and uses no unsupported management contract");
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
        using (var untrusted = await browser.GetAsync("/login/sso?returnTo=%2Fapi%2Fv1%2Fauth%2Fme"))
            Check(untrusted.StatusCode == HttpStatusCode.ServiceUnavailable && untrusted.Headers.Location is null,
                "A discovery document naming another host is never followed");
        await Control(controls, new { command = "fault", value = "invalid-client" });
        using (var invalidClient = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(invalidClient.StatusCode == HttpStatusCode.ServiceUnavailable, "Confidential client failure is an unavailable dependency rather than anonymous authorization");
        await Control(controls, new { command = "fault", value = "upstream-rate-limit" });
        using (var throttled = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(throttled.StatusCode == HttpStatusCode.TooManyRequests && throttled.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60),
                "The native auth response preserves the producer's bounded retry interval");
        await Control(controls, new { command = "fault", value = "" });
        using (var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "A recovered producer admits the actual password grant");
        await Control(controls, new { command = "spoof-stored-admin" });
        using (var admin = await browser.GetAsync("/admin/users"))
            Check(admin.StatusCode == HttpStatusCode.Forbidden && await admin.Content.ReadAsStringAsync() == "forbidden\n",
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
        Check(observed.GetProperty("passwordGrants").GetInt32() == 1 && observed.GetProperty("userinfoRequests").GetInt32() == 1,
            "The native grant reaches actual signed access-token validation after independently valid ID-token checks");
        CheckProtocol(observed);
    }

    [Test]
    [Arguments("user@example.com")]
    [Arguments("  UsEr@Example.Com  ")]
    public async Task NativeSignupResendAndPasswordResetBindTheOwningProducer(string signupEmail)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var csrf = await HttpJourney.CsrfAsync(browser, "/onboarding");
        const string initialPassword = "new-protocol-signup-123";
        var registration = new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["displayName"] = "Protocol Signup",
            ["email"] = signupEmail,
            ["password"] = initialPassword,
            ["confirmPassword"] = initialPassword,
            ["terms"] = "true",
            ["returnTo"] = "/api/v1/auth/me"
        };
        await Control(controls, new { command = "fault", value = "signup-policy" });
        using (var policy = await HttpJourney.PostFormAsync(browser, "/onboarding", registration))
        {
            var policyHtml = await policy.Content.ReadAsStringAsync();
            Check(policy.StatusCode == HttpStatusCode.UnprocessableEntity && policyHtml.Contains("Choose a different password.", StringComparison.Ordinal) &&
                !policyHtml.Contains("That password is too common.", StringComparison.Ordinal),
                "The native password field maps the known producer error code without exposing producer text");
            Check(!(await policy.Content.ReadAsStringAsync()).Contains(initialPassword, StringComparison.Ordinal), "Rejected native signup never echoes the submitted password");
        }
        await Control(controls, new { command = "advance", seconds = 61 });
        await Control(controls, new { command = "fault", value = "" });
        using (var registered = await HttpJourney.PostFormAsync(browser, "/onboarding", registration))
        {
            var signupHtml = await registered.Content.ReadAsStringAsync();
            Check(registered.StatusCode == HttpStatusCode.OK && signupHtml.Contains("Verify your email", StringComparison.Ordinal),
                "Native registration reaches the confidential JSON signup route and starts a persistent pending-token flow");
            Check(signupHtml.Contains("name=\"email\" value=\"user@example.com\"", StringComparison.Ordinal),
                "The native signup verification form carries the canonical email after registration normalization");
        }
        Check((await Observations(controls)).GetProperty("enrollmentSecretsEncrypted").GetBoolean(),
            "Pending signup passwords and producer enrollment tokens are encrypted while awaiting verification");
        var verification = new Dictionary<string, string>
        { ["_csrf"] = csrf, ["email"] = "user@example.com", ["code"] = "000000", ["signUp"] = "1" };
        using (var wrong = await HttpJourney.PostFormAsync(browser, "/login/code/verify", verification))
            Check(wrong.StatusCode == HttpStatusCode.Unauthorized, "The owning producer rejects the incorrect signup code");
        using (var early = await HttpJourney.PostFormAsync(browser, "/login/code/resend", new() { ["_csrf"] = csrf }))
            Check(early.StatusCode == HttpStatusCode.TooManyRequests && early.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60),
                "Native resend preserves the persistent one-minute cooldown");
        await Control(controls, new { command = "advance", seconds = 61 });
        using (var resend = await HttpJourney.PostFormAsync(browser, "/login/code/resend", new() { ["_csrf"] = csrf }))
            Check(resend.StatusCode == HttpStatusCode.OK && (await resend.Content.ReadAsStringAsync()).Contains("Check your email for a new code.", StringComparison.Ordinal),
                "Resend calls the admitted signup producer again after the cooldown");
        verification["code"] = "123456";
        using (var verified = await HttpJourney.PostFormAsync(browser, "/login/code/verify", verification))
            Check(verified.StatusCode == HttpStatusCode.SeeOther && verified.Headers.Location?.OriginalString == "/api/v1/auth/me",
                "Signup verification binds the pending token and signed provider subject to the original local return path");
        var originalUser = await User(browser);
        Check(originalUser.GetProperty("email").GetString() == "user@example.com",
            "The verified native identity retains the canonical email for lowercase and mixed-case signup input");
        using (var replay = await HttpJourney.PostFormAsync(browser, "/login/code/verify", verification))
            Check(replay.StatusCode == HttpStatusCode.BadRequest, "Consumed signup state cannot verify a second time");

        using var resetBrowser = scenario.CreateClient();
        var resetCsrf = await HttpJourney.CsrfAsync(resetBrowser, "/reset-password");
        using (var started = await HttpJourney.PostFormAsync(resetBrowser, "/reset-password", new()
        { ["_csrf"] = resetCsrf, ["email"] = "user@example.com" }))
            Check(started.StatusCode == HttpStatusCode.OK, "Native password reset requests its confidential JSON mail operation");
        const string resetPassword = "reset-protocol-password-123";
        var reset = new Dictionary<string, string>
        {
            ["_csrf"] = resetCsrf,
            ["email"] = "user@example.com",
            ["code"] = "000000",
            ["password"] = resetPassword,
            ["confirmPassword"] = resetPassword
        };
        using (var wrong = await HttpJourney.PostFormAsync(resetBrowser, "/reset-password/complete", reset))
            Check(wrong.StatusCode == HttpStatusCode.Unauthorized, "Incorrect reset code cannot change the provider password");
        reset["code"] = "123456";
        reset["confirmPassword"] = "mismatched-provider-password";
        var operationsBeforeMismatch = (await Observations(controls)).GetProperty("actions").GetArrayLength();
        using (var mismatch = await HttpJourney.PostFormAsync(resetBrowser, "/reset-password/complete", reset))
            Check(mismatch.StatusCode == HttpStatusCode.UnprocessableEntity, "Native reset validates matching passwords before contacting the producer");
        Check((await Observations(controls)).GetProperty("actions").GetArrayLength() == operationsBeforeMismatch,
            "A mismatched reset form has no producer side effect");
        reset["confirmPassword"] = resetPassword;
        using (var completed = await HttpJourney.PostFormAsync(resetBrowser, "/reset-password/complete", reset))
            Check(completed.StatusCode == HttpStatusCode.SeeOther && completed.Headers.Location?.OriginalString == "/login?email=user%40example.com&notice=password-reset",
                "Successful reset completes the real owning JSON contract and returns its native sign-in notice");
        using (var ended = await browser.GetAsync("/api/v1/auth/me"))
            Check(ended.StatusCode == HttpStatusCode.Unauthorized, "Password reset authoritatively revokes the earlier signup grant");
        using (var oldPassword = await HttpJourney.LoginAsync(resetBrowser, password: initialPassword, returnTo: "/api/v1/auth/me"))
            Check(oldPassword.StatusCode == HttpStatusCode.Unauthorized, "The previous password cannot obtain a grant after reset");
        using (var newPassword = await HttpJourney.LoginAsync(resetBrowser, password: resetPassword, returnTo: "/api/v1/auth/me"))
            Check(newPassword.StatusCode == HttpStatusCode.SeeOther, "The reset password signs in through the real headless grant");
        Check((await User(resetBrowser)).GetProperty("userId").GetString() == originalUser.GetProperty("userId").GetString(),
            "Reset retains the same verified provider identity");
        var observed = await Observations(controls);
        Check(observed.GetProperty("actions").EnumerateArray().Count(item => item.GetString() == "sign-up") == 3,
            "Producer observations confirm policy validation, initial signup and resend");
        Check(observed.GetProperty("signupGrantBindings").GetInt32() == 1,
            "Exactly one native verification redeemed the mandatory owning enrollment token");
        Check(observed.GetProperty("enrollmentSecretsEncrypted").GetBoolean(), "Submitted signup and reset secrets remain absent from persisted plaintext");
        CheckProtocol(observed);
    }

    [Test]
    public async Task NativePasswordGuardsBindCurrentIdentityAndValidateTheCodeFormBeforeMutation()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        using var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "Password change starts with an admitted browser grant");
        var csrf = await HttpJourney.CsrfAsync(browser, "/profile?tab=security");
        await PasswordError(browser, csrf, false, new() { ["currentPassword"] = "wrong-current-password" }, "Current password is incorrect.");
        await Control(controls, new { command = "fault", value = "password-substitution" });
        await PasswordError(browser, csrf, false, new() { ["currentPassword"] = "test-password" }, "Current password is incorrect.");
        Check(!(await Observations(controls)).GetProperty("actions").EnumerateArray().Any(item => item.GetString() == "password-reset/start"),
            "Failed or foreign reauthentication sends no reset code to another identity");
        await Control(controls, new { command = "fault", value = "" });
        using var started = await StartPasswordForm(browser, csrf);
        Check(started.StatusCode == HttpStatusCode.SeeOther && started.Headers.Location?.OriginalString == "/profile?tab=security&status=code-sent",
            "The current password requests a reset code for this session's email");
        foreach (var (password, confirmation, error) in new[] {
            ("short", "short", "Use at least 12 characters."),
            (new string('é', 513), new string('é', 513), "Use a password with 1024 bytes or fewer."),
            ("replacement-password", "other-password", "Passwords do not match.") })
            await PasswordError(browser, csrf, true, new() { ["code"] = "123456", ["newPassword"] = password, ["confirmPassword"] = confirmation }, error);
        var beforeCode = (await Observations(controls)).GetProperty("actions").EnumerateArray().Count(item => item.GetString() == "password-reset/complete");
        Check(beforeCode == 0, "Local password validation never reaches the producer");
        await PasswordError(browser, csrf, true, new() { ["code"] = "000000", ["newPassword"] = "replacement-password", ["confirmPassword"] = "replacement-password" },
            "The code is incorrect or has expired.");
        await Control(controls, new { command = "fault", value = "reset-password-policy" });
        await PasswordError(browser, csrf, true, new() { ["code"] = "123456", ["newPassword"] = "replacement-password", ["confirmPassword"] = "replacement-password" },
            "Choose a different password.");
        await Control(controls, new { command = "fault", value = "" });
        var observed = await Observations(controls);
        Check(observed.GetProperty("actions").EnumerateArray().Count(item => item.GetString() == "password-reset/complete") == 2,
            "Both code and password-policy refusals reach the canonical reset-complete operation");
        Check(observed.GetProperty("managementRequests").GetInt32() == 0 && observed.GetProperty("managementMutations").GetInt32() == 0,
            "The canonical code flow never calls an unsupported password management route");
        Check(observed.GetProperty("revocations").GetInt32() == 2, "Foreign and admitted current-password proof families are revoked");
        Check(observed.GetProperty("enrollmentSecretsEncrypted").GetBoolean(), "Submitted passwords never enter plaintext state");
        _ = await User(browser);
        CheckProtocol(observed);
    }

    [Test]
    public async Task AbsentResetStartReportsUnavailableWithoutChangingThePasswordOrEndingTheBrowser()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "A signed-in browser precedes the missing reset route");
        var cookie = SessionCookie(signedIn);
        var csrf = await HttpJourney.CsrfAsync(browser, "/profile?tab=security");
        await Control(controls, new { command = "fault", value = "reset-start-missing" });
        using (var unavailable = await StartPasswordForm(browser, csrf)) await PasswordUnavailable(unavailable, "test-password");
        using (var retained = await Request(replica, "/api/v1/auth/me", cookie))
            Check(retained.StatusCode == HttpStatusCode.OK, "A missing reset route leaves the original browser family active");
        var observed = await Observations(controls);
        Check(observed.GetProperty("actions").EnumerateArray().Count(item => item.GetString() == "password-reset/start") == 1 &&
            observed.GetProperty("revocations").GetInt32() == 1 && observed.GetProperty("managementRequests").GetInt32() == 0,
            "One reset start failed, only its extra proof family was revoked, and no management fallback ran");
        await Control(controls, new { command = "fault", value = "" });
        using var credentials = scenario.CreateClient();
        using var original = await HttpJourney.LoginAsync(credentials, returnTo: "/api/v1/auth/me");
        Check(original.StatusCode == HttpStatusCode.SeeOther, "The original password remains unchanged");
        CheckProtocol(await Observations(controls));
    }

    [Test]
    public async Task CorrectResetCodeInvalidatesBrowserAndPatFamiliesAcrossReplicas()
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
        var csrf = await HttpJourney.CsrfAsync(browser, "/profile?tab=security");
        using var started = await StartPasswordForm(browser, csrf);
        Check(started.StatusCode == HttpStatusCode.SeeOther, "Current-password proof sends the owning reset code");
        await ActivePat(replica, pat);
        const string replacement = "code-confirmed-password-123";
        using var completed = await CompletePasswordForm(browser, csrf, replacement);
        Check(completed.StatusCode == HttpStatusCode.SeeOther && completed.Headers.Location?.OriginalString == "/login",
            "Only confirmed reset completion ends the browser");
        using (var ended = await Request(replica, "/api/v1/auth/me", cookie))
            Check(ended.StatusCode == HttpStatusCode.Unauthorized, "The previous browser is refused on another replica");
        using var patRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        patRequest.Headers.Authorization = new("Bearer", pat);
        using var endedPat = await replica.SendAsync(patRequest);
        Check(endedPat.StatusCode == HttpStatusCode.Unauthorized, "The independent PAT family lost owning issuer authority");
        using var credentials = scenario.CreateClient();
        using var oldPassword = await HttpJourney.LoginAsync(credentials, returnTo: "/api/v1/auth/me");
        Check(oldPassword.StatusCode == HttpStatusCode.Unauthorized, "Reset replaced the original password");
        using var newPassword = await HttpJourney.LoginAsync(credentials, password: replacement, returnTo: "/api/v1/auth/me");
        Check(newPassword.StatusCode == HttpStatusCode.SeeOther, "The confirmed new password obtains a grant");
        var observed = await Observations(controls);
        Check(observed.GetProperty("managementRequests").GetInt32() == 0, "Reset uses no invented PATCH or revoke-all contract");
        CheckProtocol(observed);
    }

    [Test]
    public async Task LostPasswordResetReplyNeverReplaysTheMutationOrReportsSuccess()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        using var signedIn = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther, "The native request starts from an admitted browser grant");
        var csrf = await HttpJourney.CsrfAsync(browser, "/profile?tab=security");
        using var started = await StartPasswordForm(browser, csrf);
        Check(started.StatusCode == HttpStatusCode.SeeOther, "The code was requested before completion");
        const string replacement = "lost-reset-reply-password-123";
        await Control(controls, new { command = "fault", value = "reset-drop-completion-response" });
        using (var lost = await CompletePasswordForm(browser, csrf, replacement)) await PasswordUnavailable(lost, replacement);
        var observed = await Observations(controls);
        Check(observed.GetProperty("actions").EnumerateArray().Count(item => item.GetString() == "password-reset/complete") == 1 &&
            observed.GetProperty("managementRequests").GetInt32() == 0,
            "The producer applied one reset before losing delivery; no replay or privileged fallback ran");
        using (var ended = await browser.GetAsync("/api/v1/auth/me"))
            Check(ended.StatusCode == HttpStatusCode.Unauthorized, "The next request observes the producer's actual revocation despite the lost reply");
        await Control(controls, new { command = "fault", value = "" });
        using var credentials = scenario.CreateClient();
        using var changed = await HttpJourney.LoginAsync(credentials, password: replacement, returnTo: "/api/v1/auth/me");
        Check(changed.StatusCode == HttpStatusCode.SeeOther, "Producer evidence confirms the reported uncertain reset happened");
        CheckProtocol(await Observations(controls));
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

    [Test]
    public async Task PersistentNativeAttemptBudgetsShareTheirLastSlotAcrossReplicas()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var first = scenario.CreateClient();
        using var second = scenario.CreateClient(replica: true);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            using var denied = await HttpJourney.LoginAsync(attempt % 2 == 0 ? first : second, password: "wrong-password", returnTo: "/api/v1/auth/me");
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "The shared persisted budget admits each remaining credential attempt");
        }
        var firstFinalSlot = HttpJourney.LoginAsync(first, password: "wrong-password", returnTo: "/api/v1/auth/me");
        var secondFinalSlot = HttpJourney.LoginAsync(second, password: "wrong-password", returnTo: "/api/v1/auth/me");
        using var firstFinal = await firstFinalSlot;
        using var secondFinal = await secondFinalSlot;
        var statuses = new[] { firstFinal.StatusCode, secondFinal.StatusCode };
        Check(statuses.Count(status => status == HttpStatusCode.Unauthorized) == 1 && statuses.Count(status => status == HttpStatusCode.TooManyRequests) == 1,
            "Two actual replicas atomically share the one remaining account/IP attempt");
        var throttled = firstFinal.StatusCode == HttpStatusCode.TooManyRequests ? firstFinal : secondFinal;
        Check(throttled.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(30), "The original account budget supplies its exact native retry interval");
        Check((await Observations(controls)).GetProperty("passwordGrants").GetInt32() == 10,
            "The throttled last-slot request never submits its password to the producer");
        await Control(controls, new { command = "advance", seconds = 30 });
        using (var recovered = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me"))
            Check(recovered.StatusCode == HttpStatusCode.SeeOther, "The real clock-bound budget admits a correct grant after its wait");
        _ = await User(first);
        using (var forgiven = await HttpJourney.LoginAsync(second, password: "wrong-password", returnTo: "/api/v1/auth/me"))
            Check(forgiven.StatusCode == HttpStatusCode.Unauthorized, "Successful authentication forgives the shared account budget on the other replica");
        var observed = await Observations(controls);
        Check(observed.GetProperty("passwordGrants").GetInt32() == 12, "Only admitted real attempts reached the producer before and after forgiveness");
        CheckProtocol(observed);
    }

    private static Task<HttpResponseMessage> StartPasswordForm(HttpClient browser, string csrf) =>
        HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/profile/password", new()
        { ["_csrf"] = csrf, ["currentPassword"] = "test-password" });

    private static Task<HttpResponseMessage> CompletePasswordForm(HttpClient browser, string csrf, string replacement) =>
        HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/profile/password/complete", new()
        { ["_csrf"] = csrf, ["code"] = "123456", ["newPassword"] = replacement, ["confirmPassword"] = replacement });

    private static async Task PasswordUnavailable(HttpResponseMessage response, string replacement)
    {
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.Location is null,
            "An absent or uncertain provider operation never produces the password-change success redirect");
        Check(!response.Headers.TryGetValues("Set-Cookie", out var cookies) || cookies.All(value => !value.StartsWith("ts-dashboard-session=;", StringComparison.Ordinal)),
            "An unavailable provider operation does not claim that the current browser signed out");
        var html = await response.Content.ReadAsStringAsync();
        Check(html.Contains("Service unavailable", StringComparison.Ordinal) && !html.Contains(replacement, StringComparison.Ordinal) &&
            !html.Contains("sk_management-key", StringComparison.Ordinal) && !html.Contains("Secret vendor payload", StringComparison.Ordinal),
            "The native dependency page reveals neither submitted passwords nor provider credentials and errors");
    }

    private static async Task ActivePat(HttpClient replica, string pat)
    {
        var response = await HttpJourney.McpAsync(replica, pat, "tools/call", new { name = "get_profile", arguments = new { } });
        var result = response.GetProperty("result");
        Check(!result.TryGetProperty("isError", out var error) || !error.GetBoolean(), "The independent native-issued PAT still has actual remote authority");
        Check(result.GetProperty("structuredContent").GetProperty("email").GetString() == "user@example.com",
            "The preserved PAT resolves its own owning grant and profile across replicas");
    }

    private static async Task PasswordError(HttpClient browser, string csrf, bool complete, Dictionary<string, string> fields, string error)
    {
        fields["_csrf"] = csrf;
        using var rejected = await HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/profile/password" + (complete ? "/complete" : ""), fields);
        Check(rejected.StatusCode == HttpStatusCode.SeeOther && rejected.Headers.Location?.OriginalString == "/profile?tab=security&error=" + Uri.EscapeDataString(error),
            "The canonical password form reports its known safe validation error: " + error);
        using var profile = await browser.GetAsync(rejected.Headers.Location);
        Check(profile.StatusCode == HttpStatusCode.OK, "A rejected password form retains the current browser session");
        var html = await profile.Content.ReadAsStringAsync();
        Check(html.Contains(error, StringComparison.Ordinal) && !html.Contains("Secret vendor payload", StringComparison.Ordinal),
            "The native profile exposes only the known safe error");
    }

    private static async Task<Uri> Authorize(HttpClient browser, HttpClient producer)
    {
        using var started = await browser.GetAsync("/login/sso?returnTo=%2Fapi%2Fv1%2Fauth%2Fme&prompt=login");
        Check(started.StatusCode == HttpStatusCode.SeeOther && started.Headers.Location is { Scheme: "https", Host: "auth.terrascale.tech", AbsolutePath: "/terrascale/oauth/authorize" },
            "Native SSO selects the discovered trusted authorization endpoint");
        var target = started.Headers.Location!;
        // ts-auth answers login_required to prompt=login, so a legacy /login/sso?prompt=login link must not forward it.
        Check(!target.Query.Contains("prompt", StringComparison.Ordinal), "Native SSO sends no prompt ts-auth refuses");
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
