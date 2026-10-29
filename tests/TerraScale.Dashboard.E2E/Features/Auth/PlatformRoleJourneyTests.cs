using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class PlatformRoleJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task RoleGrantAndRemovalAreRereadBeforeEachNativeOperatorRequest()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "A real producer operator signs in through the native password grant");
            var cookie = Cookie(login);
            Check((await Observe(producer)).GetProperty("issuedAdminRoleGrants").GetInt32() == 1, "The original signed-in grant actually carried the producer's operator role");
            await Control(producer, new { command = "platform-role", value = "" });
            using (var removed = await browser.GetAsync("/admin/users"))
            {
                var body = await removed.Content.ReadAsStringAsync();
                Check(removed.StatusCode == HttpStatusCode.Forbidden && removed.Content.Headers.ContentType?.MediaType == "text/html" &&
                    body.Contains("Your account does not have platform admin access.", StringComparison.Ordinal) &&
                    body.Contains(removed.Headers.GetValues("X-Request-ID").Single(), StringComparison.Ordinal),
                    "Removal is reflected with a styled denial and request ID before a cached operator token can authorize");
            }
            Check((await Observe(producer)).GetProperty("refreshes").GetInt32() == 1, "Role removal is checked by a real persisted rotation");

            await Control(producer, new { command = "platform-role", value = "admin" });
            await Control(producer, new { command = "fault", value = "access-multi-audience" });
            using (var granted = await browser.GetAsync("/admin/users"))
            {
                await Unavailable(granted);
            }

            Check((await Observe(producer)).GetProperty("refreshes").GetInt32() == 2,
                "A later promotion is read from another new grant, including the issuer among legitimate resource audiences");
            await Control(producer, new { command = "fault", value = "" });
            await Control(producer, new { command = "platform-role", value = "" });
            using (var otherReplica = await Request(replica, "/admin/users", cookie))
            {
                Check(otherReplica.StatusCode == HttpStatusCode.Forbidden, "The other running replica also rereads removal before granting authority");
            }

            using (var stillAuthenticated = await browser.GetAsync("/api/v1/auth/me"))
            {
                Check(stillAuthenticated.StatusCode == HttpStatusCode.OK, "An ordinary active identity remains signed in after losing only its operator role");
                Check((await Json(stillAuthenticated)).GetProperty("role").GetString() == "owner", "Platform authority does not rewrite the selected organization role");
            }
            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 3, "Each later operator authorization obtains current producer role evidence");
            Protocol(observed);
        }

        [Test]
        public async Task VerifiedOperatorsDoNotInventUnpublishedAdminOperations()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The native browser has a producer-authorized operator grant");
            string[] pages = ["/admin", "/admin/users", "/admin/organizations", "/admin/projects", "/admin/databases", "/admin/estate", "/admin/usage", "/admin/plans", "/admin/pricing"];
            string[] actions = ["/api/v1/admin/users/foreign-account", "/api/v1/admin/organizations/foreign-account/foreign-org",
                "/api/v1/admin/projects/foreign-account/foreign-org/foreign-project", "/api/v1/admin/estate/action", "/api/v1/admin/plans",
                "/api/v1/admin/plans/foreign-plan/visibility", "/api/v1/admin/plans/foreign-plan/delete"];
            foreach (var path in pages)
            {
                using var response = await browser.GetAsync(path);
                await Unavailable(response);
            }
            foreach (var path in actions)
            {
                using var response = await browser.PostAsync(path, new StringContent("an unparsed target and form"));
                await Unavailable(response);
            }
            using (var head = await browser.SendAsync(new(HttpMethod.Head, "/admin/users")))
            {
                Check(head.StatusCode == HttpStatusCode.ServiceUnavailable && await head.Content.ReadAsStringAsync() == "", "HEAD also reports unavailable without inventing a rendered inventory");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == pages.Length + actions.Length + 1,
                "Every distinct native operation verifies a newly rotated grant");
            Check(observed.GetProperty("managementRequests").GetInt32() == 0 && observed.GetProperty("managementMutations").GetInt32() == 0,
                "Operator status never borrows a service key to perform an unpublished user or estate mutation");
            Protocol(observed);
        }

        [Test]
        public async Task ConcurrentNativeOperatorRequestsShareTheSinglePersistedRotation()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The concurrent requests share an actual operator browser grant");
            var cookie = Cookie(login);
            await Control(producer, new { command = "pause-refresh" });
            var first = browser.GetAsync("/admin/users");
            await WaitForRotation(producer);
            await Control(producer, new { command = "pause-next-userinfo" });
            var second = Request(replica, "/admin/projects", cookie);
            await WaitForObservation(producer, "userinfoPauses", 1);
            Check(!second.IsCompleted && (await Observe(producer)).GetProperty("refreshes").GetInt32() == 1,
                "The replica has observed the original token and is held at its owning UserInfo boundary");
            await Control(producer, new { command = "release-refresh" });
            using var firstResponse = await first;
            await Unavailable(firstResponse);
            Check(!second.IsCompleted, "The first grant is persisted before the replica's initial activity answer is delivered");
            await Control(producer, new { command = "release-userinfo" });
            using var secondResponse = await second;
            await Unavailable(secondResponse);
            Check((await Observe(producer)).GetProperty("refreshes").GetInt32() == 1, "Both concurrent callers share one single-use rotating grant");
            using (var later = await browser.GetAsync("/admin/users"))
            {
                await Unavailable(later);
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 2, "A later caller must rotate again rather than reuse earlier authorization evidence");
            Protocol(observed);
        }

        [Test]
        public async Task AConsistentSignedDescendantIssuerCannotBecomeTheFirstPartyTenant()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await Control(producer, new { command = "platform-role", value = "admin" });
            await Control(producer, new { command = "fault", value = "descendant-issuer" });
            using (var login = await HttpJourney.LoginAsync(browser, returnTo: "/admin/users"))
            {
                Check(login.StatusCode == HttpStatusCode.Unauthorized, "Native login rejects a signed identity whose discovery issuer is a descendant of the owning tenant");
                Check(!login.Headers.TryGetValues("Set-Cookie", out var cookies) || cookies.All(value => !value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)),
                    "Consistent foreign tenant evidence never creates a dashboard session");
            }
            using (var admin = await browser.GetAsync("/admin/users"))
            {
                Check(admin.StatusCode == HttpStatusCode.SeeOther && admin.Headers.Location?.OriginalString == "/login?returnTo=%2Fadmin%2Fusers",
                    "The descendant issuer grants no native platform authority");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("publishedIssuer").GetString() == "https://auth.terrascale.tech/terrascale/descendant" &&
                observed.GetProperty("descendantIssuerGrants").GetInt32() == 1 && observed.GetProperty("issuedAdminRoleGrants").GetInt32() == 1,
                "Discovery, signed identity/access claims and the producer's UserInfo verifier consistently use the same descendant issuer and actual private admin role");
            Check(observed.GetProperty("refreshes").GetInt32() == 0 && observed.GetProperty("managementRequests").GetInt32() == 0,
                "Exact tenant admission rejects the grant before any privileged rotation or operation");
            Protocol(observed);
        }

        [Test]
        [Arguments("access-wrong-issuer")]
        [Arguments("access-wrong-audience")]
        [Arguments("access-bad-signature")]
        [Arguments("access-identity-token")]
        [Arguments("access-expired")]
        public async Task InvalidNewAccessEvidenceCannotAuthorizeAnOperator(string fault)
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The original operator token is valid before the producer fault");
            var cookie = Cookie(login);
            await Control(producer, new { command = "fault", value = fault });
            using (var denied = await browser.GetAsync("/admin/users"))
            {
                Check(denied.StatusCode == HttpStatusCode.SeeOther && denied.Headers.Location?.OriginalString == "/login?returnTo=%2Fadmin%2Fusers",
                    "Invalid fresh access-token evidence ends authentication before granting operator authority: " + fault);
            }

            await Control(producer, new { command = "fault", value = "" });
            using (var ended = await Request(replica, "/api/v1/auth/me", cookie))
            {
                Check(ended.StatusCode == HttpStatusCode.Unauthorized, "Rejected rotated evidence cannot recover on a replica");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 1 && observed.GetProperty("managementRequests").GetInt32() == 0,
                "Rejected rotating evidence is single use and reaches no privileged operation");
            Protocol(observed);
        }

        [Test]
        public async Task IdTokenLocalProfileAndOrganizationRolesNeverBecomePlatformAuthority()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await Control(producer, new { command = "fault", value = "forged-id-admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "A valid identity token authenticates despite carrying an extra untrusted privilege field");
            await Control(producer, new { command = "spoof-stored-admin" });
            await Control(producer, new { command = "workspace-role", role = "admin" });
            using (var context = await browser.GetAsync("/api/v1/auth/me"))
            {
                Check(context.StatusCode == HttpStatusCode.OK, "The ordinary account's current membership is available");
                Check((await Json(context)).GetProperty("role").GetString() == "admin", "The producer fixture actually changed the organization role to admin");
            }
            using (var forbidden = await browser.GetAsync("/admin/users"))
            {
                Check(forbidden.StatusCode == HttpStatusCode.Forbidden, "Neither the ID token, persisted profile nor organization role grants platform administration");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("forgedIdRoleTokens").GetInt32() == 1 && observed.GetProperty("storedClaimSpoof").GetBoolean() &&
                observed.GetProperty("issuedAdminRoleGrants").GetInt32() == 0 && observed.GetProperty("refreshes").GetInt32() == 1,
                "The spoofed inputs are present, but the actual fresh access token carries no platform authority");
            Protocol(observed);
        }

        [Test]
        [Arguments("jwks-substitution")]
        [Arguments("access-role-type")]
        public async Task ActivityAloneCannotAuthorizeUnverifiableOrMalformedRoleEvidence(string fault)
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The original operator grant has valid signature and identity evidence");
            await Control(producer, new { command = "fault", value = fault });
            using (var denied = await browser.GetAsync("/admin/users"))
            {
                Check(denied.StatusCode == HttpStatusCode.Forbidden, "Current activity does not substitute for a verified role claim: " + fault);
            }

            using (var stillActive = await browser.GetAsync("/api/v1/auth/me"))
            {
                Check(stillActive.StatusCode == HttpStatusCode.OK, "The owning userinfo independently validated the rotated access token's activity");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 1 && observed.GetProperty("managementRequests").GetInt32() == 0,
                "The new active grant is checked locally against published keys and exact claim type before any privilege");
            Protocol(observed);
        }

        [Test]
        public async Task ActiveOperatorWithoutARefreshGrantCannotClaimCurrentAuthority()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await Control(producer, new { command = "platform-role", value = "admin" });
            await Control(producer, new { command = "fault", value = "no-refresh" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "A valid primary grant may authenticate without a rotating grant");
            using (var unavailable = await browser.GetAsync("/admin/users"))
            {
                Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "Current operator authority cannot be established from a cached access token alone");
            }

            using (var active = await browser.GetAsync("/api/v1/auth/me"))
            {
                Check(active.StatusCode == HttpStatusCode.OK, "Lack of fresh privilege evidence preserves the still-active ordinary identity");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 0 && observed.GetProperty("managementRequests").GetInt32() == 0,
                "No guessed refresh token or borrowed management credential is submitted");
            Protocol(observed);
        }

        [Test]
        public async Task RepeatedOldOperatorAccessCannotSatisfyForcedRotation()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The old access token actually starts with an operator role");
            var cookie = Cookie(login);
            await Control(producer, new { command = "platform-role", value = "" });
            await Control(producer, new { command = "fault", value = "repeat-access" });
            using (var repeated = await browser.GetAsync("/admin/users"))
            {
                Check(repeated.StatusCode == HttpStatusCode.ServiceUnavailable, "An unchanged old operator JWT cannot masquerade as freshly rotated role evidence");
            }

            await Control(producer, new { command = "fault", value = "" });
            await Control(producer, new { command = "advance", seconds = 61 });
            using (var expiredLease = await Request(replica, "/admin/users", cookie))
            {
                Check(expiredLease.StatusCode == HttpStatusCode.SeeOther, "An ambiguous expired refresh lease ends the session instead of replaying its secret");
            }

            using (var gone = await browser.GetAsync("/api/v1/auth/me"))
            {
                Check(gone.StatusCode == HttpStatusCode.Unauthorized, "The rejected ambiguous grant is removed on every replica");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 1 && observed.GetProperty("managementRequests").GetInt32() == 0,
                "The consumed refresh secret is never retried and its old role authorizes no operation");
            Protocol(observed);
        }

        [Test]
        public async Task LostOperatorRefreshResponseCannotReplayOrRestoreAuthority()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "A genuine operator begins the native request");
            var cookie = Cookie(login);
            await Control(producer, new { command = "drop-refresh-response" });
            using (var dropped = await browser.GetAsync("/admin/users"))
            {
                Check(dropped.StatusCode == HttpStatusCode.ServiceUnavailable, "A producer-rotated grant with lost delivery cannot grant privilege");
            }

            await Control(producer, new { command = "advance", seconds = 61 });
            using (var expired = await Request(replica, "/admin/users", cookie))
            {
                Check(expired.StatusCode == HttpStatusCode.SeeOther, "The replica refuses an expired uncertain privilege lease");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 1, "The potentially consumed rotating secret is never submitted twice");
            Protocol(observed);
        }

        [Test]
        public async Task LogoutWinsOverAnInflightForcedOperatorRotation()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            await Control(producer, new { command = "platform-role", value = "admin" });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            Check(login.StatusCode == HttpStatusCode.SeeOther, "A producer operator signs in before the concurrent logout");
            var cookie = Cookie(login);
            var csrf = await HttpJourney.CsrfAsync(browser);
            await Control(producer, new { command = "pause-refresh" });
            var pending = browser.GetAsync("/admin/users");
            await WaitForRotation(producer);
            using (var signedOut = await HttpJourney.PostFormAsync(browser, "/api/v1/auth/logout", new() { ["_csrf"] = csrf }))
            {
                Check(signedOut.StatusCode == HttpStatusCode.SeeOther, "Native logout revokes the same token family and deletes its persistent browser record");
                // Browsers apply form-action to the redirect that follows the sign-out form.
                var formAction = signedOut.Headers.GetValues("Content-Security-Policy").Single().Split(';')
                    .Select(directive => directive.Trim()).Single(directive => directive.StartsWith("form-action ", StringComparison.Ordinal)).Split(' ');
                Check(signedOut.Headers.Location is { IsAbsoluteUri: true, Scheme: "https", Host: "auth.terrascale.tech", AbsolutePath: "/terrascale/oauth/logout" } &&
                    formAction.Contains("https://auth.terrascale.tech"), "The sign-out form may follow its redirect to ts-auth's end-session endpoint");
            }
            await Control(producer, new { command = "release-refresh" });
            using (var late = await pending)
            {
                Check(late.StatusCode == HttpStatusCode.SeeOther, "The late operator response cannot resurrect a signed-out browser");
            }

            using (var gone = await Request(replica, "/api/v1/auth/me", cookie))
            {
                Check(gone.StatusCode == HttpStatusCode.Unauthorized, "The captured cookie also remains revoked on the second replica");
            }

            var observed = await Observe(producer);
            Check(observed.GetProperty("refreshes").GetInt32() == 1 && observed.GetProperty("revocations").GetInt32() == 1,
                "One real rotation and one real family revocation occur, with no replay or privileged mutation");
            Protocol(observed);
        }

        private static async Task Unavailable(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.CacheControl?.NoStore == true &&
                body.Contains("Platform administration is unavailable.", StringComparison.Ordinal) &&
                body.Contains(response.Headers.GetValues("X-Request-ID").Single(), StringComparison.Ordinal),
                "Verified operator authority reports the absent owning operation contract explicitly and without a fabricated inventory");
        }

        private static Task WaitForRotation(HttpClient producer)
        {
            return WaitForObservation(producer, "refreshes", 1);
        }

        private static async Task WaitForObservation(HttpClient producer, string property, int expected)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if ((await Observe(producer)).GetProperty(property).GetInt32() == expected)
                {
                    return;
                }

                await Task.Delay(50);
            }
            throw new InvalidOperationException("The native operator request did not reach its external producer barrier: " + property);
        }

        private static async Task Control(HttpClient producer, object command)
        {
            using var response = await producer.PostAsJsonAsync("/__test/control", command);
            Check(response.StatusCode == HttpStatusCode.OK, "The external producer accepted isolated scenario setup");
        }

        private static async Task<JsonElement> Observe(HttpClient producer)
        {
            using var response = await producer.GetAsync("/__test/observations");
            Check(response.StatusCode == HttpStatusCode.OK, "The external producer exposes its observed network contract");
            return await Json(response);
        }

        private static async Task<JsonElement> Json(HttpResponseMessage response)
        {
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

        private static void Protocol(JsonElement observed)
        {
            Check(observed.GetProperty("contractFailures").GetInt32() == 0 && observed.GetProperty("unsupportedIntrospectionRequests").GetInt32() == 0,
                "Current origin, client and UserInfo contracts are preserved without unsupported introspection calls");
            Check(observed.GetProperty("credentialsEncrypted").GetBoolean(), "Every rotated grant remains encrypted in persistent dashboard documents");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
