using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TUnit.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Accounts;

[ClassDataSource<DashboardRootFixture>]
public sealed class StateSdkJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("sessions")]
    [Arguments("auth_credentials")]
    [Arguments("dashboard_accounts")]
    [Arguments("dashboard_account_identities")]
    [Arguments("dashboard_organizations")]
    public async Task AnExternalUpdateIsVisibleInsideTheWaitingReadRequest(string collection)
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        Check(await scenario.StartHostsAsync(), "The fixed state schema initializes.");
        var state = scenario.RegistrationState;
        await state.PutAsync(collection, "synthetic-lease", JsonSerializer.SerializeToElement(new { lease = "held" }), TerraBasePrecondition.Absent, default);
        var original = (await state.GetAsync(collection, "synthetic-lease", default))!;
        var rotate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // This task belongs to a different request, outside the waiting request's timing context.
        var writer = Task.Run(async () =>
        {
            await rotate.Task;
            await state.PutAsync(collection, "synthetic-lease", JsonSerializer.SerializeToElement(new { lease = "rotated" }),
                TerraBasePrecondition.Matches(original.Version), default);
        });
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            var before = (await state.GetAsync(collection, "synthetic-lease", default))!;
            await Assert.That(before.Document.GetProperty("lease").GetString()).IsEqualTo("held");
            rotate.SetResult();
            await writer;
            var after = (await state.GetAsync(collection, "synthetic-lease", default))!;
            await Assert.That(after.Document.GetProperty("lease").GetString()).IsEqualTo("rotated");
        }, NullLogger.Instance);
    }

    [Test]
    public async Task ReadinessUsesOneSchemaPageAndConsumesPaginationWithoutCachingDroppedCollections()
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        Check(await scenario.StartHostsAsync(), "The fixed state schema initializes.");
        using var client = scenario.CreateClient();
        var before = (await ObserveAsync(controls)).GetProperty("collectionLists").GetInt32();
        await ReadyAsync(client);
        Check((await ObserveAsync(controls)).GetProperty("collectionLists").GetInt32() == before + 1,
            "A readiness probe checks all required collections through one SDK metadata page.");
        await ControlAsync(controls, new { command = "mode", value = "paged-collections" });
        await ReadyAsync(client);
        Check((await ObserveAsync(controls)).GetProperty("collectionLists").GetInt32() == before + 4,
            "A producer returning ten collections per page is fully traversed.");
        await ControlAsync(controls, new { command = "drop-collection", name = "auth-flows" });
        using var missing = await client.GetAsync("/readyz");
        Check(missing.StatusCode == HttpStatusCode.ServiceUnavailable, "The next probe immediately refuses a missing collection.");
        CheckProtocol(await ObserveAsync(controls));
    }

    [Test]
    public async Task ConcurrentColdStartNativeSignupCasAndColdRestartUseTheProductionStateSdk()
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        await ControlAsync(controls, new { command = "pause-collections" });
        var startup = scenario.StartHostsAsync(simultaneous: true);
        await WaitAsync(controls, "pausedCreates", 2);
        await ControlAsync(controls, new { command = "release-collections" });
        Check(await startup, "Both actual dashboard replicas create missing collections in the producer-seeded platform database: " + scenario.StartupError);
        var boot = await ObserveAsync(controls);
        CheckSchema(boot);
        Check(boot.GetProperty("databaseCreates").GetInt32() == 0 && boot.GetProperty("databasePuts").GetInt32() == 0 &&
            boot.GetProperty("collectionConflicts").GetInt32() >= 1, "The dashboard never creates or replaces the seeded platform database; concurrent collection name_taken conflicts are reread through the real SDK.");
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        await ReadyAsync(client); await ReadyAsync(replica);
        Check((await ObserveAsync(controls)).GetProperty("documentWrites").GetInt32() == 0, "Readiness only reads every fixed collection and writes no test or sentinel documents.");

        var account = await NativeAccountAsync(client);
        using (var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password))
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The other replica reads the remotely persisted new account and encrypted session state.");
        var secret = await HttpJourney.CreatePatAsync(client, "Persistent state E2E", account.Password);
        var profileCsrf = await HttpJourney.CsrfAsync(client, "/profile");
        var localeCsrf = await HttpJourney.CsrfAsync(replica, "/profile");
        var beforeRace = await ObserveAsync(controls);
        await ControlAsync(controls, new { command = "pause-account-writes" });
        var name = HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile", new()
        { ["_csrf"] = profileCsrf, ["name"] = "Persisted across replicas", ["phoneNumber"] = "+5511999999999" });
        var locale = HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/profile/language", new()
        { ["_csrf"] = localeCsrf, ["locale"] = "pt-BR" });
        await WaitAsync(controls, "pausedWrites", 2);
        await ControlAsync(controls, new { command = "release-account-writes" });
        using (var savedName = await name) Check(savedName.StatusCode == HttpStatusCode.SeeOther, "The native profile mutation commits.");
        using (var savedLocale = await locale) Check(savedLocale.StatusCode == HttpStatusCode.SeeOther, "The native locale mutation commits after an actual producer CAS race.");
        var raced = await ObserveAsync(controls);
        Check(raced.GetProperty("preconditionFailures").GetInt32() > beforeRace.GetProperty("preconditionFailures").GetInt32(), "The external producer rejected at least one stale version; the app refetched instead of overwriting another field.");
        var me = await MeAsync(replica);
        Check(me.GetProperty("name").GetString() == "Persisted across replicas" && me.GetProperty("locale").GetString() == "pt-BR" &&
            me.GetProperty("phoneNumber").GetString() == "+5511999999999", "Both concurrent native changes remain visible on the other app replica.");

        string queryId;
        using (var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/saved-queries", new()
        { ["_csrf"] = profileCsrf, ["name"] = "Remote saved query", ["sql"] = "SELECT * FROM users LIMIT 10" }))
        {
            Check(saved.StatusCode == HttpStatusCode.Created, "Project saved-query state is written through the production state SDK.");
            using var json = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()); queryId = json.RootElement.GetProperty("id").GetString()!;
        }
        var rpc = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "get_profile", arguments = new { } });
        Check(rpc.GetProperty("result").GetProperty("structuredContent").GetProperty("name").GetString() == "Persisted across replicas",
            "Current-protocol MCP resolves its native-issued PAT and fresh profile through real remote encrypted state.");
        var persisted = await ObserveAsync(controls);
        Check(Count(persisted, "sessions") == 2 && Count(persisted, "auth_credentials") == 1 && Count(persisted, "dashboard_saved_queries") == 1 &&
            Count(persisted, "dashboard_account_identities") == 1, "Sessions, separate PAT grant, saved query and public identity claim are durably represented in the real producer protocol fixture.");

        Check(await scenario.RestartAsync(), "Two cold replacement app hosts bootstrap the existing remote foundation.");
        using var coldClient = scenario.CreateReplacementClient(client);
        using var coldReplica = scenario.CreateReplacementClient(replica, replica: true);
        var restarted = await ObserveAsync(controls);
        Check(restarted.GetProperty("documentDigest").GetString() == persisted.GetProperty("documentDigest").GetString() &&
            restarted.GetProperty("collections").GetRawText() == persisted.GetProperty("collections").GetRawText() &&
            restarted.GetProperty("collectionPosts").GetInt32() == persisted.GetProperty("collectionPosts").GetInt32(),
            "Cold startup preserves every existing document/version and collection identity without replacing records or recreating collections.");
        await ReadyAsync(coldClient); await ReadyAsync(coldReplica);
        Check((await MeAsync(coldClient)).GetProperty("accountId").GetString() == me.GetProperty("accountId").GetString(), "A pre-restart browser cookie resolves the same persisted public account identity.");
        var queries = await ManagementListJourney.ReadListAllAsync(coldReplica, "/api/v1/dashboard/data/saved-queries");
        Check(queries.EnumerateArray().Single().GetProperty("id").GetString() == queryId, "Saved-query identity survives both app processes being replaced.");
        rpc = await HttpJourney.McpAsync(coldReplica, secret, "tools/call", new { name = "get_profile", arguments = new { } });
        Check(rpc.GetProperty("result").GetProperty("structuredContent").GetProperty("locale").GetString() == "pt-BR", "The native-issued PAT remains valid on a cold replacement replica.");

        var beforeReady = await ObserveAsync(controls);
        await ReadyAsync(coldClient);
        var afterReady = await ObserveAsync(controls);
        Check(afterReady.GetProperty("collectionLists").GetInt32() == beforeReady.GetProperty("collectionLists").GetInt32() + 1 &&
            afterReady.GetProperty("privateRequests").GetInt32() > beforeReady.GetProperty("privateRequests").GetInt32(),
            "Readiness checks the required schema in one collection listing through the private listener.");
        Check((await MeAsync(coldReplica)).GetProperty("email").GetString() == account.Email &&
            (await ObserveAsync(controls)).GetProperty("privateRequests").GetInt32() > afterReady.GetProperty("privateRequests").GetInt32(),
            "Encrypted account/session reads independently use the private listener without a durable API key or public data bearer.");
        using (var deleted = await HttpJourney.PostFormAsync(coldReplica, "/api/v1/dashboard/data/saved-queries/delete", new()
        { ["_csrf"] = localeCsrf, ["id"] = queryId }))
            Check(deleted.StatusCode == HttpStatusCode.NoContent, "Native cleanup deletes the saved query through an opaque producer If-Match.");
        CheckProtocol(await ObserveAsync(controls));
    }

    [Test]
    public async Task InterruptedBootstrapResumesAndReadinessNeverRepairsSchemaOrMislabelsMissingCollectionsAs429()
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        await ControlAsync(controls, new { command = "mode", value = "interrupted" });
        Check(!await scenario.StartHostsAsync(), "A producer outage during creation prevents the actual app from admitting traffic.");
        var partial = await ObserveAsync(controls);
        Check(partial.GetProperty("collections").GetArrayLength() is > 0 and < 16, "The interrupted cold start leaves real, partial producer metadata.");
        await ControlAsync(controls, new { command = "mode", value = "" });
        Check(await scenario.StartHostsAsync(), "The next actual app startup resumes existing collections and establishes the remainder.");
        var recovered = await ObserveAsync(controls);
        CheckSchema(recovered);
        foreach (var previous in partial.GetProperty("collections").EnumerateArray())
        {
            var current = recovered.GetProperty("collections").EnumerateArray().Single(item => item.GetProperty("name").GetString() == previous.GetProperty("name").GetString());
            Check(current.GetProperty("id").GetString() == previous.GetProperty("id").GetString() &&
                current.GetProperty("createdAt").GetInt64() == previous.GetProperty("createdAt").GetInt64(), "Resumed startup preserves existing collection identity and creation time.");
        }
        using var client = scenario.CreateClient();
        await ReadyAsync(client);
        await ControlAsync(controls, new { command = "drop-collection", name = "auth-flows" });
        var missing = await ObserveAsync(controls);
        using (var readiness = await client.GetAsync("/readyz")) Check(readiness.StatusCode == HttpStatusCode.ServiceUnavailable, "One missing required collection fails readiness even while the other14 exist.");
        using (var login = await HttpJourney.LoginAsync(client)) Check(login.StatusCode == HttpStatusCode.ServiceUnavailable, "A genuine schema404 propagates as a dependency503 before any redirect to ts-auth.");
        var checkedSchema = await ObserveAsync(controls);
        Check(checkedSchema.GetProperty("collectionPosts").GetInt32() == missing.GetProperty("collectionPosts").GetInt32() &&
            checkedSchema.GetProperty("documentWrites").GetInt32() == missing.GetProperty("documentWrites").GetInt32(),
            "Neither readiness nor a failed native sign-in creates missing schema or writes fallback state.");
        Check(await scenario.RestartAsync(), "Automatic startup establishes the missing fixed collection on cold replacement.");
        using var coldClient = scenario.CreateReplacementClient(client);
        await ReadyAsync(coldClient);
        await ControlAsync(controls, new { command = "mode", value = "wrong-collection" });
        using (var readiness = await coldClient.GetAsync("/readyz")) Check(readiness.StatusCode == HttpStatusCode.ServiceUnavailable, "Readiness rejects a mismatched returned collection identity.");
        CheckProtocol(await ObserveAsync(controls));
    }

    [Test]
    [Arguments("authorization")]
    [Arguments("forbidden")]
    [Arguments("project-missing")]
    [Arguments("region-unavailable")]
    [Arguments("secret-delivery-unavailable")]
    [Arguments("invalid-auth-client-secret")]
    [Arguments("invalid-session-key")]
    [Arguments("invalid-peer-secret")]
    [Arguments("redirect")]
    [Arguments("wrong-collection")]
    [Arguments("bad-conflict-reread")]
    [Arguments("other-conflict")]
    [Arguments("wrong-missing-code")]
    public async Task ColdStartRejectsUnadmittedAuthorityAndUnrelatedProducerFailures(string failure)
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        await ControlAsync(controls, new { command = "mode", value = failure });
        Check(!await scenario.StartHostsAsync(), "Actual hosted startup must fail closed for " + failure);
        var observation = await ObserveAsync(controls);
        Check(observation.GetProperty("documentWrites").GetInt32() == 0 && scenario.StartupError is not null,
            "Bootstrap failure cannot write user state, select a fallback store or silently admit traffic: " + failure);
        if (failure is "authorization" or "forbidden" or "project-missing" or "region-unavailable" or "secret-delivery-unavailable" or "invalid-auth-client-secret" or "invalid-session-key" or "invalid-peer-secret" or "redirect" or "wrong-missing-code")
            Check(observation.GetProperty("collectionPosts").GetInt32() == 0, "Only a genuine missing collection in the validated fixed database can trigger schema creation.");
        Check(observation.GetProperty("redirectTargetRequests").GetInt32() == 0 && scenario.ForeignCredentials == 0,
            "A state-listener redirect is never followed, so the peer credential reaches no other destination.");
        CheckProtocol(observation);
    }

    [Test]
    public async Task PeerCredentialReachesOnlyTheStateListener()
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        Check(await scenario.StartHostsAsync(), "The dashboard starts with the delivered peer credential: " + scenario.StartupError);
        using var client = scenario.CreateClient();
        await ReadyAsync(client);
        Check((await ObserveAsync(controls)).GetProperty("privateRequests").GetInt32() > 0, "Startup and readiness reached the state listener with the credential.");

        // A public TerraBase origin and a vendor origin stand in for every non-state destination.
        foreach (var destination in new[] { new Uri("https://api.terrascale.tech/healthz"), new Uri("https://api.stripe.com/v1/customers"), new Uri(scenario.ProviderOrigin, "/__test/redirected") })
            Check(await scenario.SendStateClientElsewhereAsync(destination) == nameof(HttpRequestException),
                "The credentialed state client refuses " + destination.Host + " before sending.");
        var observation = await ObserveAsync(controls);
        Check(observation.GetProperty("redirectTargetRequests").GetInt32() == 0 && scenario.ForeignCredentials == 0,
            "No request outside the state listener carried the peer credential.");

        var (handlers, followsRedirects) = scenario.ProductionStateHandlers();
        Check(handlers.Contains(nameof(TerraScale.Dashboard.Platform.Persistence.TerraBasePeerCredential)) && !followsRedirects,
            "Production's state client attaches the credential and its primary handler follows no redirect: " + string.Join(",", handlers));
        CheckProtocol(observation);
    }

    [Test]
    public async Task AStalledPlanPublishNeverHoldsADashboardView()
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        Check(await scenario.StartHostsAsync(), "The dashboard starts against the producer: " + scenario.StartupError);
        using var client = scenario.CreateClient();
        await ReadyAsync(client);
        await NativeAccountAsync(client);
        var organization = await scenario.UpgradeOnlyOrganizationAsync(OrganizationRecord.StarterPlanId);
        await ControlAsync(controls, new { command = "pause-plans" });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (var home = await client.GetAsync("/"))
            Check(home.StatusCode == HttpStatusCode.OK && clock.Elapsed >= TimeSpan.FromSeconds(1.5) && clock.Elapsed < TimeSpan.FromSeconds(10),
                "A view answers within the publisher's own deadline while TerraBase's plan route stalls (QA newuser:8): " + clock.Elapsed);
        await ControlAsync(controls, new { command = "release-plans" });
        await scenario.ReconcilePlansAsync();
        var observed = await ObserveAsync(controls);
        Check(observed.GetProperty("organizationPlans").TryGetProperty(organization, out var plan) && plan.GetString() == "starter",
            "The reconciler still publishes the plan the stalled view gave up on.");
    }

    [Test]
    public async Task PlanReconcilerPublishesEveryOrganizationsPlanAgainAfterATerraBaseWipe()
    {
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        Check(await scenario.StartHostsAsync(), "The dashboard starts against the producer: " + scenario.StartupError);
        using var client = scenario.CreateClient();
        await ReadyAsync(client);
        await NativeAccountAsync(client);
        var organization = await scenario.UpgradeOnlyOrganizationAsync(OrganizationRecord.StarterPlanId);
        static string? Plan(JsonElement observed, string id) =>
            observed.GetProperty("organizationPlans").TryGetProperty(id, out var plan) ? plan.GetString() : null;

        await scenario.ReconcilePlansAsync();
        Check(Plan(await ObserveAsync(controls), organization) == "starter", "One reconciler pass publishes a Starter organization's plan to TerraBase by organization id.");
        // A TerraBase wipe drops every plan; nothing in billing changes, and no one opens the dashboard.
        await ControlAsync(controls, new { command = "wipe-plans" });
        await scenario.ReconcilePlansAsync();
        var observed = await ObserveAsync(controls);
        Check(Plan(observed, organization) == "starter", "The next reconciler pass restores the Starter plan TerraBase lost.");
        Check(observed.GetProperty("failures").GetArrayLength() == 0, "Every plan publish and state list followed the producer contract: " + observed.GetProperty("failures").GetRawText());
        // Each catalog plan names the TerraBase tier with its allowances; an unpaid organization keeps Hobby.
        var tiers = PlanCatalog.Plans.Select(plan => TerraScale.Dashboard.Platform.Persistence.TerraBasePlanSync.PlanFor(new OrganizationRecord { PlanId = plan.Id, BillingStatus = "active" }));
        Check(string.Join(",", tiers) == "hobby,starter,pro,team", "Free, Starter, Pro and Team publish hobby, starter, pro and team: " + string.Join(",", tiers));
        Check(TerraScale.Dashboard.Platform.Persistence.TerraBasePlanSync.PlanFor(new OrganizationRecord { PlanId = OrganizationRecord.StarterPlanId, BillingStatus = "billing_required" }) == "hobby",
            "An organization that has not paid keeps Hobby.");
    }

    [Test]
    public async Task AtomicAdmissionRollbackAndOpaquePagingSurviveTwoHostsAndColdRestart()
    {
        // This is the installed SDK against a test-only HTTP protocol producer, NOT a deployed TerraBase qualification.
        await using var scenario = await StateSdkScenario.PrepareAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        Check(await scenario.StartHostsAsync(), "Both actual hosts initialize real SDK persistence.");
        using var client = scenario.CreateClient(); using var replica = scenario.CreateClient(replica: true);
        await ReadyAsync(client); await ReadyAsync(replica);
        await AdmissionAsync(client, new { command = "seed", key = "natural-seed" });
        var assertions = await AdmissionAsync(replica, new { command = "assert", key = "natural-seed" });
        Check(assertions.GetProperty("results")[2].GetProperty("count").GetInt32() == 1, "Filter, absence and count assertions read pre-write state.");
        await AdmissionAsync(client, new { command = "duplicate", key = "natural-seed" }, HttpStatusCode.BadRequest);
        foreach (var command in new[] { "too-many", "too-many-counts", "too-large", "wildcard" })
            await AdmissionAsync(client, new { command, key = "rejected" }, HttpStatusCode.BadRequest);
        await AdmissionAsync(client, new { command = "absent-conflict", key = "absent-rejected" }, HttpStatusCode.PreconditionFailed);
        await AdmissionAsync(client, new { command = "stale-delete" }, HttpStatusCode.PreconditionFailed);
        var failed = await AdmissionAsync(client, new { command = "rollback", key = "rolled-back" }, HttpStatusCode.PreconditionFailed);
        Check(failed.GetProperty("operationIndex").GetInt32() == 1, "The SDK preserves the failing operation index.");
        var capacity = await AdmissionAsync(client, new { command = "capacity", key = "capacity-rejected" }, HttpStatusCode.ServiceUnavailable);
        Check(capacity.GetProperty("operationIndex").GetInt32() == 3 && capacity.GetProperty("errorCode").GetString() == "capacity", "Producer captured-byte refusal preserves SDK status, error code and operation index for individually valid documents.");
        var rejected = await AdmissionAsync(replica, new { command = "read-capacity", key = "capacity-rejected" });
        Check(rejected.GetProperty("client").ValueKind == JsonValueKind.Null && rejected.GetProperty("grants").EnumerateArray().All(grant => grant.ValueKind == JsonValueKind.Null),
            "Captured-byte refusal leaves neither the candidate client nor any of its three grants.");
        var rollback = await AdmissionAsync(replica, new { command = "read", key = "rolled-back" });
        Check(rollback.GetProperty("client").ValueKind == JsonValueKind.Null && rollback.GetProperty("budget").GetProperty("document").GetProperty("count").GetInt32() == 0,
            "A late stale-version failure leaves neither the candidate client nor a budget increment.");
        await ControlAsync(controls, new { command = "pause-transactions" });
        var first = client.PostAsJsonAsync("/__test/state-admission", new { command = "admit", key = "candidate-one" });
        var second = replica.PostAsJsonAsync("/__test/state-admission", new { command = "admit", key = "candidate-two" });
        await WaitAsync(controls, "pausedTransactions", 2);
        await ControlAsync(controls, new { command = "release-transactions" });
        using var one = await first; using var two = await second;
        Check(new[] { one.StatusCode, two.StatusCode }.Order().SequenceEqual(new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed }),
            "Two hosts contending for one slot commit exactly one atomic client/budget/grant transaction.");
        var winner = one.IsSuccessStatusCode ? "candidate-one" : "candidate-two";
        var stored = await AdmissionAsync(replica, new { command = "read", key = winner });
        Check(stored.GetProperty("client").GetProperty("id").GetString() == winner && stored.GetProperty("budget").GetProperty("document").GetProperty("count").GetInt32() == 1,
            "Natural-key point read maps once and returns the sole committed budget increment.");
        var before = await ObserveAsync(controls);
        Check(Count(before, "dashboard_mcp_oauth_clients") == 2 && Count(before, "dashboard_mcp_oauth_codes") == 2, "No losing transaction's client or grant survives.");
        await AdmissionAsync(replica, new { command = "noise", key = "unrelated" });
        await AdmissionAsync(client, new { command = "assert", key = "unrelated" }, HttpStatusCode.PreconditionFailed);
        var page = await AdmissionAsync(client, new { command = "page", filtered = true });
        var cursor = page.GetProperty("next_cursor").GetString();
        Check(page.GetProperty("items").GetArrayLength() == 1 && cursor is not null, "Query returns a single bounded page with opaque continuation.");
        await AdmissionAsync(replica, new { command = "page", filtered = false, cursor }, HttpStatusCode.BadRequest);
        Check(await scenario.RestartAsync(), "Both app hosts restart over the unchanged remote producer.");
        using var cold = scenario.CreateClient();
        var next = await AdmissionAsync(cold, new { command = "page", filtered = true, cursor });
        Check(next.GetProperty("items").GetArrayLength() == 1 && next.GetProperty("next_cursor").ValueKind == JsonValueKind.Null &&
            next.GetProperty("items")[0].GetProperty("id").GetString() != page.GetProperty("items")[0].GetProperty("id").GetString(),
            "The unchanged filtered continuation resumes after cold replacement without duplicate IDs.");
        var listed = await AdmissionAsync(cold, new { command = "page", filtered = false });
        var listIds = new List<string>();
        while (true)
        {
            listIds.Add(listed.GetProperty("items")[0].GetProperty("id").GetString()!);
            var continuation = listed.GetProperty("next_cursor").GetString();
            if (continuation is null) break;
            listed = await AdmissionAsync(cold, new { command = "page", filtered = false, cursor = continuation });
        }
        Check(listIds.Count == 3 && listIds.Distinct().Count() == 3 && listIds.Contains(page.GetProperty("items")[0].GetProperty("id").GetString()!),
            "List continuation visits all provider IDs once, while filtered query excludes the unrelated row.");
        await AdmissionAsync(cold, new { command = "remove" });
        await AdmissionAsync(cold, new { command = "delete" });
        var after = await ObserveAsync(controls);
        Check(Count(after, "dashboard_mcp_oauth_clients") == 2 && Count(after, "dashboard_mcp_oauth_codes") == 1, "Listed delete bypasses natural-key mapping; natural-key delete uses the current opaque version.");
        Check(await scenario.SendStateClientElsewhereAsync(new Uri("https://api.terrascale.tech/healthz")) == nameof(HttpRequestException) && scenario.ForeignCredentials == 0,
            "All transaction/query requests stayed on the credentialed private origin; a public destination is rejected before transport.");
        CheckProtocol(after);
    }

    private static async Task<JsonElement> AdmissionAsync(HttpClient client, object command, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__test/state-admission") { Content = JsonContent.Create(command) };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Check(response.StatusCode == expected, "Admission harness status: " + response.StatusCode + " expected " + expected + ": " + text);
        if (string.IsNullOrEmpty(text)) return default;
        using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
    }

    private static async Task<(string Email, string Password)> NativeAccountAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "state-sdk-" + suffix + "@example.test"; var password = "State-SDK-" + suffix;
        using (var verified = await HttpJourney.LoginAsync(client, email, password, "/onboarding", signUp: true))
            Check(verified.StatusCode == HttpStatusCode.SeeOther, "Hosted sign-up creates the persistent account/session records.");
        await StepAsync(new() { ["step"] = "account-information", ["displayName"] = "State SDK" });
        await StepAsync(new() { ["step"] = "payment-method", ["bypass"] = "1" });
        await StepAsync(new() { ["step"] = "project", ["name"] = "State SDK project", ["region"] = "GRU" });
        return (email, password);
        async Task StepAsync(Dictionary<string, string> form)
        {
            form["_csrf"] = await HttpJourney.CsrfAsync(client, "/onboarding");
            using var response = await HttpJourney.PostFormAsync(client, "/onboarding/step", form);
            Check(response.StatusCode == HttpStatusCode.SeeOther && response.Headers.Location?.OriginalString is "/onboarding" or "/", "Native onboarding commits the actual next step: " + form["step"]);
        }
    }

    private static async Task ReadyAsync(HttpClient client)
    { using var response = await client.GetAsync("/readyz"); Check(response.StatusCode == HttpStatusCode.OK, "The real schema is ready on this app replica."); }
    private static async Task<JsonElement> MeAsync(HttpClient client)
    { using var response = await client.GetAsync("/api/v1/auth/me"); response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static async Task ControlAsync(HttpClient client, object command)
    { using var response = await client.PostAsJsonAsync("/__test/control", command); response.EnsureSuccessStatusCode(); }
    private static async Task<JsonElement> ObserveAsync(HttpClient client)
    { using var response = await client.GetAsync("/__test/observations"); response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static async Task WaitAsync(HttpClient client, string counter, int expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await ObserveAsync(client)).GetProperty(counter).GetInt32() < expected) await Task.Delay(25, deadline.Token);
    }
    private static int Count(JsonElement observation, string name) => observation.GetProperty("collections").EnumerateArray().Single(collection => collection.GetProperty("name").GetString() == name).GetProperty("documents").GetInt32();
    private static void CheckSchema(JsonElement observation)
    {
        var expected = new[] { "sessions", "account_deletions", "dashboard_deletion_cleanup", "dashboard_account_enrollments", "auth_credentials", "session_indexes", "auth-flows", "dashboard_accounts", "dashboard_account_identities", "dashboard_organizations", "dashboard_access_tokens", "dashboard_access_token_hashes", "dashboard_saved_queries", "dashboard_sql_workspaces", "dashboard_sql_database_deletions", "dashboard_billing", "dashboard_internal_billing", "dashboard_stripe_customers", "dashboard_billing_changes", "dashboard_stripe_checkouts", "dashboard_allowance_alerts", "dashboard_alert_rules", "dashboard_alert_projects", "dashboard_log_drains", "dashboard_log_drain_projects", "dashboard_mcp_oauth_clients", "dashboard_mcp_oauth_codes" };
        Check(observation.GetProperty("collections").EnumerateArray().Select(collection => collection.GetProperty("name").GetString()).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal)), "The external producer contains exactly the fixed production collections, including deletion continuations, allowance alerts, alert rules, log drains, internal billing, account identity, checkout binding indexes and MCP OAuth clients and codes.");
    }
    private static string Token(string html)
    { var token = Regex.Match(html, "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value; Check(token.Length > 0, "The native form publishes a CSRF token."); return WebUtility.HtmlDecode(token); }
    private static void CheckProtocol(JsonElement observation) => Check(observation.GetProperty("failures").GetArrayLength() == 0,
        "The external state producer observed no contract violation: " + observation.GetProperty("failures").GetRawText());
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
