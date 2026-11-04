using System.Net;
using System.Text.Json;

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
            Check(observed.GetProperty("credentialsEncrypted").GetBoolean(), "Every rotated grant remains encrypted in persistent dashboard storage");
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
