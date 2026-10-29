using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AccountIdentityJourneyTests(DashboardRootFixture fixture)
    {
        private const string ImportedId = "01J1PQAQ7DH2KG61BZE9FR4YC9";

        [Test]
        public async Task ImportedPublicIdentitySurvivesNativeGrantsProfileChangesAndReplicaMcp()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            var seed = await Control(producer, new { command = "seed-account", accountId = ImportedId });
            using var password = scenario.CreateClient();
            using (var login = await HttpJourney.LoginAsync(password, returnTo: "/api/v1/auth/me"))
            {
                Check(login.StatusCode == HttpStatusCode.SeeOther, "An imported, indexed source account admits its owning hosted sign-in");
            }

            var user = await User(password);
            CheckIdentity(user, seed);

            using var replica = scenario.CreateClient(replica: true);
            using (var login = await HttpJourney.LoginAsync(replica, returnTo: "/api/v1/auth/me"))
            {
                Check(login.StatusCode == HttpStatusCode.SeeOther, "The second running replica completes its own hosted sign-in for the same account");
            }

            CheckIdentity(await User(replica), seed);

            var csrf = await HttpJourney.CsrfAsync(replica, "/profile");
            using (var saved = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/profile", new()
            {
                ["_csrf"] = csrf,
                ["name"] = "Imported profile updated",
                ["phoneNumber"] = "+5511999990000",
                ["id"] = "01J1PQAQ7DH2KG61BZE9FR4YCA",
                ["accountId"] = "foreign-account",
                ["userId"] = "foreign-subject"
            }))
            {
                Check(saved.StatusCode == HttpStatusCode.SeeOther, "Native profile updates ignore forged identity fields");
            }

            var updated = await User(password);
            CheckIdentity(updated, seed);
            Check(updated.GetProperty("name").GetString() == "Imported profile updated", "A separate browser rereads the profile mutation from persistent state");
            var secret = await HttpJourney.CreatePatAsync(password, "Imported account E2E");
            var profile = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "get_profile", arguments = new { } });
            var profileContent = profile.GetProperty("result").GetProperty("structuredContent");
            Check(profileContent.GetProperty("accountId").GetString() == ImportedId &&
                profileContent.GetProperty("userId").GetString() == seed.GetProperty("subject").GetString() &&
                profileContent.GetProperty("phone").GetString() == "+5511999990000" &&
                profileContent.GetProperty("name").GetString() == "Imported profile updated",
                "A native-created PAT exposes the original Go profile contract and unchanged imported identity on another running replica");
            Check(profileContent.GetProperty("mfaEnabled").ValueKind == JsonValueKind.Null && profileContent.GetProperty("passkeys").ValueKind == JsonValueKind.Null,
                "Unavailable real provider identity controls are explicit rather than fabricated disabled/empty records");
            Check(profileContent.GetProperty("accessTokens").EnumerateArray().Any(token => token.GetProperty("name").GetString() == "Imported account E2E"),
                "The profile reports actual native-created PAT metadata without its secret");
            Check(!profileContent.GetRawText().Contains(secret, StringComparison.Ordinal), "The read-only profile never returns a PAT secret");
            var context = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "get_context", arguments = new { } });
            var content = context.GetProperty("result").GetProperty("structuredContent");
            Check(content.GetProperty("user").GetProperty("id").GetString() == seed.GetProperty("subject").GetString() &&
                content.GetProperty("selection").GetProperty("organizationId").GetString() == updated.GetProperty("organizationId").GetString() &&
                content.GetProperty("selection").GetProperty("projectId").GetString() == updated.GetProperty("projectId").GetString(),
                "MCP preserves the original Go user and nested saved selection contract");
            Check(content.GetProperty("user").GetProperty("platformAdmin").ValueKind == JsonValueKind.Null,
                "Unknown platform-admin producer status never becomes a fabricated authoritative claim");
            var scopedOrganization = content.GetProperty("organizations")[0];
            Check(scopedOrganization.GetProperty("projects")[0].GetProperty("isDefault").GetBoolean() &&
                scopedOrganization.GetProperty("canManage").GetBoolean(), "Context exposes the actual provider-backed default project and caller capabilities");
            var discovery = await HttpJourney.McpAsync(replica, secret, "tools/list", new { });
            foreach (var name in new[] { "get_profile", "get_context" })
            {
                var tool = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
                Check(tool.GetProperty("outputSchema").GetProperty("type").GetString() == "object", "Typed identity tools advertise their actual structured object schema");
            }
            var savedPhone = await Tool(replica, secret, "update_profile", new { phone = "+5511888880000" });
            Check(savedPhone.GetProperty("saved").GetBoolean() && savedPhone.EnumerateObject().Count() == 1,
                "The original update_profile accepts the optional phone field and returns only its saved receipt");
            var savedLanguage = await Tool(replica, secret, "set_language", new { locale = "pt-BR" });
            Check(savedLanguage.GetProperty("saved").GetBoolean() && savedLanguage.EnumerateObject().Count() == 1,
                "The original set_language returns its saved receipt instead of echoing a divergent profile payload");
            var changedProfile = await Tool(replica, secret, "get_profile", new { });
            Check(changedProfile.GetProperty("accountId").GetString() == ImportedId && changedProfile.GetProperty("phone").GetString() == "+5511888880000" &&
                changedProfile.GetProperty("name").GetString() == "Imported profile updated" && changedProfile.GetProperty("locale").GetString() == "pt-BR",
                "Optional profile mutation preserves the omitted display name, indexed source identity and current provider subject across replicas");
            var revocable = await HttpJourney.CreatePatAsync(password, "Imported account revocation E2E");
            var listed = await Tool(replica, secret, "list_access_tokens", new { });
            var tokenId = listed.GetProperty("accessTokens").EnumerateArray().Single(token => token.GetProperty("name").GetString() == "Imported account revocation E2E")
                .GetProperty("id").GetString()!;
            Check(listed.GetProperty("accessTokens").EnumerateArray().All(token => token.GetProperty("lastFour").GetString()!.Length == 4) &&
                !listed.GetRawText().Contains(secret, StringComparison.Ordinal) && !listed.GetRawText().Contains(revocable, StringComparison.Ordinal),
                "The original list_access_tokens wraps actual safe token metadata without either token secret");
            var revoked = await Tool(replica, secret, "revoke_access_token", new { tokenId });
            Check(revoked.GetProperty("revoked").GetBoolean() && revoked.EnumerateObject().Count() == 1,
                "The original revoke_access_token accepts tokenId and returns only the revocation receipt");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/mcp"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", revocable);
                using var rejected = await replica.SendAsync(request);
                Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "A different running replica rejects the revoked PAT immediately");
            }
            var remaining = await Tool(replica, secret, "list_access_tokens", new { });
            Check(!remaining.GetProperty("accessTokens").EnumerateArray().Any(token => token.GetProperty("id").GetString() == tokenId),
                "Revocation removes only the chosen token; the original independent PAT remains authorized");
            CheckBindings(await Observations(producer), seed.GetProperty("storageKey").GetString()!, ImportedId);
        }

        [Test]
        [Arguments("account-document-id")]
        [Arguments("account-user")]
        [Arguments("account-public-id")]
        [Arguments("malformed-public-id")]
        [Arguments("identity-document-id")]
        [Arguments("identity-user")]
        [Arguments("identity-public-id")]
        [Arguments("remove-identity")]
        public async Task UnboundOrSubstitutedIdentityRejectsReadsMutationsAndFreshGrants(string corruption)
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            _ = await Control(producer, new { command = "seed-account", accountId = ImportedId });
            using var existing = scenario.CreateClient();
            using (var login = await HttpJourney.LoginAsync(existing, returnTo: "/api/v1/auth/me"))
            {
                Check(login.StatusCode == HttpStatusCode.SeeOther, "The unchanged indexed account is authenticated before fault injection");
            }

            var csrf = await HttpJourney.CsrfAsync(existing, "/profile");
            _ = await Control(producer, new { command = "account-corruption", value = corruption });
            var before = await Observations(producer);
            using (var read = await existing.GetAsync("/api/v1/auth/me"))
            {
                await DeniedIdentity(read, "Existing native session: " + corruption);
            }

            using (var mutation = await HttpJourney.PostFormAsync(existing, "/api/v1/dashboard/profile", new()
            { ["_csrf"] = csrf, ["name"] = "Must not persist", ["phoneNumber"] = "" }))
            {
                await DeniedIdentity(mutation, "Native mutation: " + corruption);
            }

            using var fresh = scenario.CreateClient(replica: true);
            using (var login = await HttpJourney.LoginAsync(fresh, returnTo: "/api/v1/auth/me"))
            {
                await DeniedIdentity(login, "Fresh signed grant: " + corruption);
                Check(!HasSessionCookie(login), "A failed identity binding never issues a new dashboard session cookie");
            }
            using (var anonymous = await fresh.GetAsync("/api/v1/auth/me"))
            {
                Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "The rejected fresh browser stays anonymous");
            }

            var after = await Observations(producer);
            Check(after.GetProperty("revocations").GetInt32() == before.GetProperty("revocations").GetInt32() + 1,
                "The real producer revokes the newly obtained grant whose account ownership failed");
            Check(after.GetProperty("identities").GetRawText() == before.GetProperty("identities").GetRawText() &&
                after.GetProperty("accounts").GetRawText() == before.GetProperty("accounts").GetRawText(),
                "Rejected reads and writes neither repair a substituted record nor synthesize an unbound imported claim");
            Check(after.GetProperty("contractFailures").GetInt32() == 0, "Rejection and independent grant cleanup preserve the real producer contract");
        }

        [Test]
        public async Task ConcurrentEnrollmentOnTwoRunningReplicasReservesOnePublicIdentity()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var first = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            _ = await Control(producer, new { command = "pause-account-reservations" });
            var firstLogin = HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me");
            var secondLogin = HttpJourney.LoginAsync(replica, returnTo: "/api/v1/auth/me");
            await WaitFor(producer, observations => observations.GetProperty("identityAttempts").GetInt32() == 2,
                "Both actual replicas must reach independent create-only identity attempts");
            _ = await Control(producer, new { command = "release-account-reservations" });
            using var firstResponse = await firstLogin;
            using var secondResponse = await secondLogin;
            Check(firstResponse.StatusCode == HttpStatusCode.SeeOther && secondResponse.StatusCode == HttpStatusCode.SeeOther,
                "Both concurrent native grants finish after the identity CAS winner persists");
            var firstUser = await User(first);
            var secondUser = await User(replica);
            var publicId = firstUser.GetProperty("accountId").GetString()!;
            Check(CanonicalId(publicId) && publicId == secondUser.GetProperty("accountId").GetString(),
                "The two independent grant families resolve one canonical public ULID");
            var observations = await Observations(producer);
            var storageKey = observations.GetProperty("identities")[0].GetProperty("storageKey").GetString()!;
            Check(Regex.IsMatch(storageKey, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) && storageKey != publicId,
                "The private subject digest stays separate from the public account ID");
            CheckBindings(observations, storageKey, publicId);
            var csrf = await HttpJourney.CsrfAsync(replica, "/profile");
            using (var updated = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/profile", new()
            { ["_csrf"] = csrf, ["name"] = "Concurrent profile", ["phoneNumber"] = "" }))
            {
                Check(updated.StatusCode == HttpStatusCode.SeeOther, "The CAS-created account remains writable through its owning native session");
            }

            Check((await User(first)).GetProperty("accountId").GetString() == publicId,
                "A subsequent cross-replica read retains the same public identity after profile mutation");
        }

        [Test]
        public async Task InterruptedAccountProjectionReusesTheReservedIdentityAndRevokesTheFailedGrant()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var first = scenario.CreateClient();
            _ = await Control(producer, new { command = "fail-account-projection-once" });
            using (var interrupted = await HttpJourney.LoginAsync(first, returnTo: "/api/v1/auth/me"))
            {
                Check(interrupted.StatusCode == HttpStatusCode.ServiceUnavailable, "An interrupted persisted account projection reports an unavailable dependency");
                Check(!HasSessionCookie(interrupted), "An incomplete enrollment cannot issue an authenticated browser cookie");
            }
            var failed = await Observations(producer);
            Check(failed.GetProperty("accounts").GetArrayLength() == 0 && failed.GetProperty("identities").GetArrayLength() == 1 &&
                failed.GetProperty("revocations").GetInt32() == 1, "The create-only reservation survives while the failed fresh grant is revoked");
            var reservedId = failed.GetProperty("identities")[0].GetProperty("publicId").GetString()!;
            Check(CanonicalId(reservedId), "The reservation already owns its final public ULID");
            using var retry = scenario.CreateClient(replica: true);
            using (var retried = await HttpJourney.LoginAsync(retry, returnTo: "/api/v1/auth/me"))
            {
                Check(retried.StatusCode == HttpStatusCode.SeeOther, "A fresh grant on another replica resumes the interrupted account projection");
            }

            Check((await User(retry)).GetProperty("accountId").GetString() == reservedId,
                "Retry reuses the original ownership reservation rather than allocating another public identity");
            var completed = await Observations(producer);
            CheckBindings(completed, failed.GetProperty("identities")[0].GetProperty("storageKey").GetString()!, reservedId);
            Check(completed.GetProperty("projectionWrites").GetInt32() == 2 && completed.GetProperty("revocations").GetInt32() == 1,
                "Exactly one failed account projection is resumed, without revoking the successful retry");
        }

        [Test]
        public async Task AValidatedLegacyDigestIdentityKeepsItsPublicIdAndCreatesOnlyItsOwningIndex()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            var seed = await Control(producer, new { command = "seed-account", accountId = "legacy" });
            Check((await Observations(producer)).GetProperty("identities").GetArrayLength() == 0,
                "The fixture actually starts with a legacy record that has no ownership index");
            using var browser = scenario.CreateClient();
            using (var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            {
                Check(login.StatusCode == HttpStatusCode.SeeOther, "An exactly matching legacy document key and subject retain existing account behavior");
            }

            CheckIdentity(await User(browser), seed);
            var storageKey = seed.GetProperty("storageKey").GetString()!;
            CheckBindings(await Observations(producer), storageKey, storageKey);
            using var replica = scenario.CreateClient(replica: true);
            using (var relogin = await HttpJourney.LoginAsync(replica, returnTo: "/api/v1/auth/me"))
            {
                Check(relogin.StatusCode == HttpStatusCode.SeeOther, "Another replica reads the same create-only legacy ownership binding");
            }

            CheckIdentity(await User(replica), seed);
        }

        [Test]
        public async Task ASubstitutedLegacyDocumentCannotBootstrapAnOwnershipClaim()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            _ = await Control(producer, new { command = "seed-account", accountId = "legacy" });
            _ = await Control(producer, new { command = "account-corruption", value = "account-document-id" });
            using var browser = scenario.CreateClient();
            using (var denied = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            {
                await DeniedIdentity(denied, "A legacy record returned under a foreign document ID");
                Check(!HasSessionCookie(denied), "Invalid legacy ownership never creates an authenticated session");
            }
            var observed = await Observations(producer);
            Check(observed.GetProperty("identities").GetArrayLength() == 0 && observed.GetProperty("revocations").GetInt32() == 1,
                "The private-key check runs before legacy index bootstrap and the fresh grant is revoked");
        }

        private static void CheckIdentity(JsonElement user, JsonElement seed)
        {
            Check(
            user.GetProperty("accountId").GetString() == seed.GetProperty("accountId").GetString() &&
            user.GetProperty("userId").GetString() == seed.GetProperty("subject").GetString(),
            "Native identity preserves the public account ID while binding the owning provider subject");
        }

        private static void CheckBindings(JsonElement observations, string storageKey, string publicId)
        {
            var identities = observations.GetProperty("identities");
            var accounts = observations.GetProperty("accounts");
            Check(identities.GetArrayLength() == 1 && accounts.GetArrayLength() == 1 &&
                identities[0].GetProperty("storageKey").GetString() == storageKey && identities[0].GetProperty("publicId").GetString() == publicId &&
                accounts[0].GetProperty("storageKey").GetString() == storageKey && accounts[0].GetProperty("publicId").GetString() == publicId,
                "One persisted account and one create-only identity index share the exact private key and public ID");
        }

        private static bool CanonicalId(string value)
        {
            return Regex.IsMatch(value, "^[0-7][0-9A-HJKMNP-TV-Z]{25}$", RegexOptions.CultureInvariant);
        }

        private static bool HasSessionCookie(HttpResponseMessage response)
        {
            return response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
                    cookies.Any(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal));
        }

        private static async Task DeniedIdentity(HttpResponseMessage response, string action)
        {
            Check(response.StatusCode == HttpStatusCode.Unauthorized, action + " must fail closed");
            var body = await response.Content.ReadAsStringAsync();
            if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
            {
                using var problem = JsonDocument.Parse(body);
                Check(problem.RootElement.GetProperty("detail").GetString() == "Account identity does not match.",
                    action + " must retain the exact safe identity error in problem JSON");
            }
            else
            {
                Check(response.Content.Headers.ContentType?.MediaType == "text/html" &&
                    body.Contains("Account identity does not match.", StringComparison.Ordinal) && body.Contains("Sign in again", StringComparison.Ordinal),
                    action + " must render the safe identity error and recovery in HTML");
            }

            Check(body.Contains(response.Headers.GetValues("X-Request-ID").Single(), StringComparison.Ordinal),
                action + " must include the request ID");
        }

        private static async Task<JsonElement> User(HttpClient client)
        {
            using var response = await client.GetAsync("/api/v1/auth/me");
            Check(response.StatusCode == HttpStatusCode.OK, "The running app confirms an authenticated native account");
            return await Json(response);
        }

        private static async Task<JsonElement> Tool(HttpClient client, string secret, string name, object arguments)
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            Check(!result.TryGetProperty("isError", out var error) || !error.GetBoolean(), "The native MCP identity mutation succeeds: " + name);
            return result.GetProperty("structuredContent").Clone();
        }

        private static async Task<JsonElement> Control(HttpClient producer, object command)
        {
            using var response = await producer.PostAsJsonAsync("/__test/control", command);
            Check(response.StatusCode == HttpStatusCode.OK, "The external test producer accepted setup/fault injection");
            return await Json(response);
        }

        private static async Task<JsonElement> Observations(HttpClient producer)
        {
            using var response = await producer.GetAsync("/__test/observations");
            Check(response.StatusCode == HttpStatusCode.OK, "The external producer reports its observed contracts and writes");
            return await Json(response);
        }

        private static async Task WaitFor(HttpClient producer, Func<JsonElement, bool> predicate, string failure)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (predicate(await Observations(producer)))
                {
                    return;
                }

                await Task.Delay(50);
            }
            throw new InvalidOperationException(failure);
        }

        private static async Task<JsonElement> Json(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
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
