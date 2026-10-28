using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class InternalBillingPolicyJourneyTests(DashboardRootFixture fixture)
{
    private const string CapAlert = "Internal spending cap reached: estimated bill $124.40, cap $120.00. Report only; services remain active.";
    private const string MissingUsage = "Every project's monthly usage is required to compute the internal bill and evaluate its cap.";
    private const string InvalidAmount = "The internal bill exceeds the supported amount or has invalid reporting rates.";

    [Test]
    public async Task NativeCapRemainsInternalReportOnlyAndRejectsLegacyCustomerControls()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        var csrf = await HttpJourney.CsrfAsync(browser, "/usage?tab=settings");
        using (var customer = await HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/usage/internal-spending-cap", new()
        { ["_csrf"] = csrf, ["spendCap"] = "200.00" }))
            await CheckAsync(customer.StatusCode == HttpStatusCode.NotFound, "An ordinary customer cannot use the internal cap endpoint");
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var html = await browser.GetStringAsync("/usage?tab=settings");
        await CheckAsync(html.Contains("Save report-only cap", StringComparison.Ordinal) &&
            !html.Contains("id=\"paid-excess\"", StringComparison.Ordinal) &&
            !html.Contains("name=\"projCap_", StringComparison.Ordinal) && !html.Contains("name=\"dbCap_", StringComparison.Ordinal),
            "The internal form exposes only its report-only organization cap");
        csrf = await HttpJourney.CsrfAsync(browser, "/usage?tab=settings");
        using (var noCsrf = await HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/usage/internal-spending-cap", new() { ["spendCap"] = "200.00" }))
            await CheckAsync(noCsrf.StatusCode == HttpStatusCode.BadRequest, "The internal cap requires CSRF validation");
        using (var legacy = await HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/usage/internal-spending-cap", new()
        { ["_csrf"] = csrf, ["spendCap"] = "200.00", ["paidExcess"] = "true" }))
            await CheckAsync(legacy.StatusCode == HttpStatusCode.SeeOther && legacy.Headers.Location?.OriginalString == "/usage?tab=settings&saved=invalid",
                "The internal endpoint rejects legacy customer overage controls without applying the cap");
        await CheckAsync(Organization(await ObserveAsync(producer), setup.Organization).GetProperty("SpendCapCents").GetInt64() == 12000,
            "Rejected legacy controls preserve the configured report-only cap");
        await CapAsync(browser, "200.00");
        await CheckAsync(Organization(await ObserveAsync(producer), setup.Organization).GetProperty("SpendCapCents").GetInt64() == 20000,
            "The internal report-only cap still persists through its scoped endpoint");
        await ActiveAsync(browser);
        await NoStripeAsync(await ObserveAsync(producer));
    }

    [Test]
    public async Task ConfigurationAndCapAlertPersistBillingWithoutCanonicalMemberIdentities()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        var reservations = Organization(await ObserveAsync(producer), setup.Organization).GetProperty("InvitationReservations").Clone();

        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await BillingStateOnlyAsync(await ObserveAsync(producer), setup.Organization, setup.Projects, reservations);
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        var observed = await ObserveAsync(producer);
        await BillingStateOnlyAsync(observed, setup.Organization, setup.Projects, reservations);
        await CheckAsync(Alerts(observed, setup.Organization).Length == 1,
            "The live owner receives the cap alert without persisting the canonical member roster");
    }

    private static async Task BillingStateOnlyAsync(JsonElement observed, string organization, string[] projects, JsonElement reservations)
    {
        var stored = Organization(observed, organization);
        await CheckAsync(new[] { "Name", "Slug", "OwnerUserId", "Members", "PendingOwnerUserId", "PendingOwnerExpiresAt", "Invitations", "CreatedAt" }
            .All(field => !stored.TryGetProperty(field, out _)),
            "Configuration and alert writes retain no ts-auth organization or member identities");
        await CheckAsync(JsonElement.DeepEquals(stored.GetProperty("InvitationReservations"), reservations),
            "Configuration and alert writes preserve existing invitation-capacity reservations");
        var settings = stored.GetProperty("Projects").EnumerateObject().ToArray();
        await CheckAsync(settings.Select(project => project.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(projects.Order(StringComparer.Ordinal)),
            "Billing retains dashboard settings keyed by the current canonical project IDs");
        await CheckAsync(settings.All(project => project.Value.GetProperty("Region").GetString() == "GRU" &&
            !project.Value.TryGetProperty("Name", out _) && !project.Value.TryGetProperty("OrganizationId", out _) &&
            !project.Value.TryGetProperty("Slug", out _) && !project.Value.TryGetProperty("CreatedAt", out _)),
            "Billing preserves project placement settings without storing ts-auth project identities");
        await PolicyAsync(stored);
        await NoStripeAsync(observed);
    }

    [Test]
    public async Task NativeCommandsPersistCopiedPlanReportExactUtcMonthAndDeduplicateOwnerAlertAcrossRestart()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var original = Organization(await ObserveAsync(producer), setup.Organization);
        await PolicyAsync(original);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var repeated = Organization(await ObserveAsync(producer), setup.Organization);
        await CheckAsync(repeated.GetProperty("PlanId").GetString() == original.GetProperty("PlanId").GetString() &&
            repeated.GetProperty("InternalBilling").GetProperty("ConfigurationId").GetString() == original.GetProperty("InternalBilling").GetProperty("ConfigurationId").GetString(),
            "Repeating the actual configure command preserves its immutable hidden plan and committed configuration");
        var first = await RunAsync(scenario, Report(setup.Organization));
        await BillAsync(first, setup.Month);
        var beforeRestart = await ObserveAsync(producer);
        await ScopeAsync(beforeRestart, setup.Organization, setup.Projects, setup.Month);
        await CheckAsync(Alerts(beforeRestart, setup.Organization).Length == 1 && Alerts(beforeRestart, setup.Organization).Single().GetProperty("Body").GetString() == CapAlert,
            "The complete native report creates exactly one owner notification with the original currency and report-only copy");
        using var inbox = await browser.GetAsync("/notifications");
        await CheckAsync(inbox.StatusCode == HttpStatusCode.OK && (await inbox.Content.ReadAsStringAsync()).Contains(CapAlert, StringComparison.Ordinal),
            "The report-only cap alert is visible through the native owner inbox");
        using var card = await browser.GetAsync("/organization?tab=billing");
        var html = await card.Content.ReadAsStringAsync();
        await CheckAsync(card.StatusCode == HttpStatusCode.OK && new[] { "$20.00", "$104.00", "$0.40", "$124.40", "$1.00 per million", "$0.20 per GB-month", "$120.00", CapAlert }
            .All(value => html.Contains(value, StringComparison.Ordinal)),
            "The native card uses consistent dollar prices, overage rates, cap labels and alert text");
        await scenario.RestartAsync();
        using var restarted = CookieClient(scenario, setup.Cookie);
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        var afterRestart = await ObserveAsync(producer);
        await CheckAsync(Alerts(afterRestart, setup.Organization).Length == 1 &&
            Organization(afterRestart, setup.Organization).GetProperty("PlanId").GetString() == original.GetProperty("PlanId").GetString(),
            "A cold app restart rereads the same persistent plan and month alert without duplicating or revoking entitlements");
        await ActiveAsync(restarted);
        await NoStripeAsync(afterRestart);
    }

    [Test]
    [Arguments("partial")]
    [Arguments("foreign-project")]
    [Arguments("wrong-month")]
    [Arguments("incoherent-end")]
    [Arguments("overflow")]
    [Arguments("offline")]
    public async Task IncompleteForeignInvalidOrOverflowedProducerUsageCannotReportAnAvailableBillOrSendCapAlert(string mode)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await ControlAsync(producer, new { command = "internal-usage", value = mode });
        var result = await RunAsync(scenario, Report(setup.Organization));
        var bill = Json(result);
        await CheckAsync(result.Exit == 1 && result.Error == "report-internal-billing: " + MissingUsage + "\n" &&
            !bill.GetProperty("available").GetBoolean() && bill.GetProperty("reportOnly").GetBoolean() &&
            !bill.GetProperty("capReached").GetBoolean() && bill.GetProperty("error").GetString() == MissingUsage,
            "The actual CLI emits a truthful unavailable report and fails when the owning producer cannot provide a valid complete month");
        await UnknownMeteringAsync(bill);
        var secret = await HttpJourney.CreatePatAsync(browser, "Unavailable monthly usage");
        var usage = await ToolAsync(browser, secret, "get_usage", new { organizationId = setup.Organization });
        var mcpBill = usage.GetProperty("internalBill");
        await CheckAsync(!mcpBill.GetProperty("available").GetBoolean() && mcpBill.GetProperty("error").GetString() == MissingUsage,
            "Current native MCP reports the same complete-metering failure without fabricating a partial bill");
        await UnknownMeteringAsync(mcpBill);
        var observed = await ObserveAsync(producer);
        await CheckAsync(Alerts(observed, setup.Organization).Length == 0, "Unavailable billing never fabricates a cap event");
        await PolicyAsync(Organization(observed, setup.Organization));
        await NoStripeAsync(observed);
        await ActiveAsync(browser);
    }

    [Test]
    public async Task CoherentMeteringSurvivesPricingOverflowInTheWholeCommandAndCurrentMcpReport()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization, writeRate: "9223372036854775807")), setup.Organization);
        var result = await RunAsync(scenario, Report(setup.Organization));
        await CheckAsync(result.Exit == 1 && result.Error == "report-internal-billing: " + InvalidAmount + "\n",
            "A valid signed rate whose monetary estimate overflows returns the exact original amount error through the full command entry");
        await CoherentPricingFailureAsync(Json(result), setup.Month);
        var secret = await HttpJourney.CreatePatAsync(browser, "Coherent pricing overflow");
        var usage = await ToolAsync(browser, secret, "get_usage", new { organizationId = setup.Organization });
        await CoherentPricingFailureAsync(usage.GetProperty("internalBill"), setup.Month);
        var observed = await ObserveAsync(producer);
        await CheckAsync(Alerts(observed, setup.Organization).Length == 0,
            "Unavailable pricing cannot deliver a cap notification despite complete genuine metering");
        await ScopeAsync(observed, setup.Organization, setup.Projects, setup.Month);
        await NoStripeAsync(observed);
        await ActiveAsync(browser);
    }

    [Test]
    [Arguments("0x_2EE0", "+0b110_0100", "024")]
    [Arguments("0O27340", "1_0_0", "+0X14")]
    public async Task OriginalBaseZeroNumericFlagsPersistTheSamePolicyAndKnownNativeBill(string cap, string writes, string storage)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        var args = Configure(setup.Organization, cap, writes);
        args[^1] = storage;
        await SuccessAsync(await RunAsync(scenario, args), setup.Organization);
        var policy = Organization(await ObserveAsync(producer), setup.Organization);
        await CheckAsync(policy.GetProperty("SpendCapCents").GetInt64() == 12000 &&
            policy.GetProperty("InternalBilling").GetProperty("WriteCentsPerMillion").GetInt64() == 100 &&
            policy.GetProperty("InternalBilling").GetProperty("StorageCentsPerGBMonth").GetInt64() == 20,
            "The full production command entry admits original Go hexadecimal, binary, octal, signed and separator syntax into the independently known persisted values");
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        await NoStripeAsync(await ObserveAsync(producer));
    }

    [Test]
    public async Task NativeConfigurePreservesPreExistingValidationFactWithoutInventingAnyStripeCardOrCharge()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer, paymentMethodValidated: true);
        await CheckAsync(Organization(await ObserveAsync(producer), setup.Organization).GetProperty("PaymentMethodValidated").GetBoolean(),
            "The external persisted fixture already records the historical validation fact before the command runs");
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var secret = await HttpJourney.CreatePatAsync(browser, "Preserved legacy validation");
        var billing = await ToolAsync(browser, secret, "get_billing", new { organizationId = setup.Organization });
        await CheckAsync(billing.GetProperty("paymentMethod").GetProperty("validated").GetBoolean() &&
            billing.GetProperty("paymentMethod").EnumerateObject().Count() == 1 && !billing.GetProperty("hasSubscription").GetBoolean(),
            "Native configure and current MCP preserve the historical boolean without claiming any current card or Stripe subscription");
        using var checkout = await CheckoutAsync(browser);
        await CheckAsync(checkout.StatusCode == HttpStatusCode.Forbidden,
            "A preserved legacy validation fact never authorizes vendor payment for an internal report-only organization");
        var observed = await ObserveAsync(producer);
        await CheckAsync(Organization(observed, setup.Organization).GetProperty("PaymentMethodValidated").GetBoolean() &&
            observed.GetProperty("billing").GetProperty("bindings").EnumerateArray().Single().GetProperty("CustomerId").GetString() == "" &&
            observed.GetProperty("billing").GetProperty("bindings").EnumerateArray().Single().GetProperty("SubscriptionId").GetString() == "",
            "The canonical validation fact survives while internal binding has no invented vendor identities");
        await NoStripeAsync(observed);
    }

    [Test]
    public async Task LostCanonicalConfigurationReplyRetriesSamePersistedProposalWithoutNewPlanOrVendorResources()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await ControlAsync(producer, new { command = "internal-fail-put-once", collection = "dashboard_organizations", after = true });
        var ambiguous = await RunAsync(scenario, Configure(setup.Organization));
        await CheckAsync(ambiguous.Exit == 1 && ambiguous.Error == "configure-internal-billing: Internal billing dependencies are unavailable.\n",
            "A lost successful organization-write reply cannot be reported as a completed configuration");
        var interrupted = await ObserveAsync(producer);
        var committed = Organization(interrupted, setup.Organization);
        await PolicyAsync(committed);
        await CheckAsync(interrupted.GetProperty("billing").GetProperty("bindings").EnumerateArray().Single().GetProperty("State").GetString() == "reserved",
            "The acknowledged billing fence remains closed until the committed proposal is reconciled");
        await scenario.RestartAsync();
        using var restarted = CookieClient(scenario, setup.Cookie);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var recovered = await ObserveAsync(producer);
        var completed = Organization(recovered, setup.Organization);
        await CheckAsync(completed.GetProperty("PlanId").GetString() == committed.GetProperty("PlanId").GetString() &&
            completed.GetProperty("InternalBilling").GetProperty("ConfigurationId").GetString() == committed.GetProperty("InternalBilling").GetProperty("ConfigurationId").GetString() &&
            recovered.GetProperty("billing").GetProperty("bindings").EnumerateArray().Single().GetProperty("State").GetString() == "active",
            "Native retry after restart activates exactly the persisted hidden-plan proposal");
        await NoStripeAsync(recovered);
    }

    [Test]
    public async Task InterruptedOwnerInboxWriteRecoversReservedMonthAlertExactlyOnce()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await ControlAsync(producer, new { command = "internal-fail-put-once", collection = "dashboard_accounts", after = false });
        var interrupted = await RunAsync(scenario, Report(setup.Organization));
        await CheckAsync(interrupted.Exit == 1 && interrupted.Error == "report-internal-billing: Internal billing dependencies are unavailable.\n",
            "An interrupted inbox write never claims the complete report and notification were delivered");
        var before = await ObserveAsync(producer);
        await CheckAsync(Alerts(before, setup.Organization).Length == 0 &&
            Organization(before, setup.Organization).GetProperty("InternalBillingAlerts").EnumerateObject().Count() == 1,
            "The committed month reservation survives an unsuccessful owner-inbox projection");
        await scenario.RestartAsync();
        using var restarted = CookieClient(scenario, setup.Cookie);
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        await CheckAsync(Alerts(await ObserveAsync(producer), setup.Organization).Length == 1,
            "Actual command retries recover exactly one persisted notification for the month");
        await ActiveAsync(restarted);
    }

    [Test]
    public async Task RemovedFreshOperatorRoleCannotConfigureOrReadEvenAValidPreexistingInternalTarget()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        var rejected = await RunAsync(scenario, Configure(setup.Organization), beforeDecision: () =>
            ControlAsync(producer, new { command = "platform-role", value = "" }));
        await CheckAsync(rejected.Exit == 1 && rejected.Error == "configure-internal-billing: Platform administrator authorization is required.\n",
            "An old approved device identity cannot replace the role removed before its forced privileged rotation");
        var observed = await ObserveAsync(producer);
        await CheckAsync(Organization(observed, setup.Organization).GetProperty("InternalBilling").ValueKind == JsonValueKind.Null &&
            observed.GetProperty("billing").GetProperty("bindings").GetArrayLength() == 0 &&
            observed.GetProperty("billing").GetProperty("singleton").ValueKind == JsonValueKind.Null,
            "Rejected current authority leaves the hidden plan, global singleton and checkout fence absent");
        await NoStripeAsync(observed);
    }

    [Test]
    public async Task PersistedInternalReservationWinsAgainstReplicaCheckoutBeforeAnyStripeRequest()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        using var replica = CookieClient(scenario, setup.Cookie, true);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_billing", id = setup.Organization, kind = "internal", after = true });
        await using var configuring = await StartAsync(scenario, Configure(setup.Organization));
        try
        {
            await PausedAsync(producer);
            using var checkout = await CheckoutAsync(replica);
            await CheckAsync(checkout.StatusCode == HttpStatusCode.Forbidden &&
                (await checkout.Content.ReadAsStringAsync()).Contains("Internal billing reports usage without Stripe charges", StringComparison.Ordinal),
                "A native checkout on the other app replica observes the already committed internal fence");
            await NoStripeAsync(await ObserveAsync(producer));
        }
        finally { await ControlAsync(producer, new { command = "internal-release-put" }); }
        await SuccessAsync(await configuring.ResultAsync(), setup.Organization);
        await PolicyAsync(Organization(await ObserveAsync(producer), setup.Organization));
    }

    [Test]
    public async Task PersistedStripeReservationWinsAgainstInternalConfigureWithoutReplacingCheckoutOwnership()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        using var replica = CookieClient(scenario, setup.Cookie, true);
        // Finish the authenticated form read before timing the mutation's persistence barrier.
        var csrf = await HttpJourney.CsrfAsync(replica);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_billing", id = setup.Organization, kind = "stripe", after = true });
        var checkout = CheckoutAsync(replica, csrf);
        try
        {
            await PausedAsync(producer, checkout: checkout);
            var rejected = await RunAsync(scenario, Configure(setup.Organization));
            await CheckAsync(rejected.Exit == 1 && rejected.Error == "configure-internal-billing: Internal billing cannot replace Stripe billing or checkout state.\n",
                "The public operator command cannot replace the checkout's already committed Stripe fence");
            var observed = await ObserveAsync(producer);
            await CheckAsync(Organization(observed, setup.Organization).GetProperty("InternalBilling").ValueKind == JsonValueKind.Null &&
                observed.GetProperty("billing").GetProperty("singleton").ValueKind == JsonValueKind.Null &&
                observed.GetProperty("billing").GetProperty("bindings").EnumerateArray().Single().GetProperty("Kind").GetString() == "stripe",
                "The losing internal operation preserves the Stripe reservation and creates no hidden plan or global claim");
        }
        finally { await ControlAsync(producer, new { command = "internal-release-put" }); }
        using var completed = await checkout;
        await CheckAsync(completed.StatusCode == HttpStatusCode.SeeOther && completed.Headers.Location?.Host == "127.0.0.1" &&
            completed.Headers.Location.AbsolutePath == "/__test/checkout",
            "The winning native checkout completes through the official Stripe client and its real test-producer checkout URL");
        var requests = (await ObserveAsync(producer)).GetProperty("billing").GetProperty("stripeRequests").EnumerateArray().Select(row => row.GetString()).ToArray();
        await CheckAsync(requests.Count(row => row == "POST /v1/customers") == 1 && requests.Count(row => row == "POST /v1/checkout/sessions") == 1,
            "Only the winning Stripe operation creates a customer and checkout session");
    }

    [Test]
    public async Task ConcurrentPublicCommandsCanCommitInternalBillingToOnlyOneOfTwoCanonicalOrganizations()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer, 2);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_billing", kind = "internal", after = true, count = 2 });
        await using var first = await StartAsync(scenario, Configure(setup.Targets[0]));
        await using var second = await StartAsync(scenario, Configure(setup.Targets[1]));
        try { await PausedAsync(producer, 2); }
        finally { await ControlAsync(producer, new { command = "internal-release-put" }); }
        var results = await Task.WhenAll(first.ResultAsync(), second.ResultAsync());
        await CheckAsync(results.Count(row => row.Exit == 0) == 1 && results.Count(row => row.Exit == 1) == 1,
            "Actual concurrent operator commands contend for one persisted global organization claim");
        var observed = await ObserveAsync(producer);
        var singleton = observed.GetProperty("billing").GetProperty("singleton");
        var winner = singleton.GetProperty("OrganizationId").GetString()!;
        await CheckAsync(setup.Targets.Contains(winner) && setup.Targets.Count(id => Organization(observed, id).GetProperty("InternalBilling").ValueKind == JsonValueKind.Object) == 1 &&
            singleton.GetProperty("PlanId").GetString() == Organization(observed, winner).GetProperty("PlanId").GetString(),
            "The authoritative singleton agrees with the sole configured organization and immutable hidden plan");
        await PolicyAsync(Organization(observed, winner));
        await NoStripeAsync(observed);
    }

    [Test]
    public async Task NativeReconfigurationPreservesHiddenIdentityAndReportsNewCopiedRatesWithoutCapAlert()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var initial = Organization(await ObserveAsync(producer), setup.Organization);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization, "30000", "200")), setup.Organization);
        var changed = Organization(await ObserveAsync(producer), setup.Organization);
        await CheckAsync(initial.GetProperty("PlanId").GetString() == changed.GetProperty("PlanId").GetString() &&
            initial.GetProperty("InternalBilling").GetProperty("ConfigurationId").GetString() != changed.GetProperty("InternalBilling").GetProperty("ConfigurationId").GetString(),
            "A real reconfiguration copies the new rates under a new proposal while preserving the one hidden plan identity");
        var result = await RunAsync(scenario, Report(setup.Organization));
        var bill = Json(result);
        await CheckAsync(result.Exit == 0 && bill.GetProperty("available").GetBoolean() && bill.GetProperty("writeCents").GetInt64() == 20800 &&
            bill.GetProperty("storageCents").GetInt64() == 40 && bill.GetProperty("totalCents").GetInt64() == 22840 &&
            bill.GetProperty("spendCapCents").GetInt64() == 30000 && !bill.GetProperty("capReached").GetBoolean(),
            "The public report uses the current copied contract and exact measured overages instead of stale configuration rates");
        await CheckAsync(Alerts(await ObserveAsync(producer), setup.Organization).Length == 0, "A measured estimate below the current cap creates no cap notification");
        using var billing = await browser.GetAsync("/organization?tab=billing");
        var body = await billing.Content.ReadAsStringAsync();
        await CheckAsync(billing.StatusCode == HttpStatusCode.OK && body.Contains("Internal computed bill", StringComparison.Ordinal) && body.Contains("No Stripe charges", StringComparison.Ordinal),
            "Native billing renders the persisted report-only policy and truthful absence of Stripe charges");
        await NoStripeAsync(await ObserveAsync(producer));
    }

    [Test]
    public async Task RoleRemovedAfterPersistedGlobalClaimStopsPolicyCommitAndNewApprovedCommandSafelyResumes()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_internal_billing", after = true });
        await using var running = await StartAsync(scenario, Configure(setup.Organization));
        try
        {
            await PausedAsync(producer);
            await ControlAsync(producer, new { command = "platform-role", value = "" });
        }
        finally { await ControlAsync(producer, new { command = "internal-release-put" }); }
        var rejected = await running.ResultAsync();
        await CheckAsync(rejected.Exit == 1 && rejected.Error == "configure-internal-billing: Platform administrator authorization is required.\n",
            "The command forces fresh persisted PAT rotation after reserving billing and before writing policy");
        var reserved = await ObserveAsync(producer);
        var claimedPlan = reserved.GetProperty("billing").GetProperty("singleton").GetProperty("PlanId").GetString();
        await CheckAsync(Organization(reserved, setup.Organization).GetProperty("InternalBilling").ValueKind == JsonValueKind.Null &&
            reserved.GetProperty("billing").GetProperty("bindings").EnumerateArray().Single().GetProperty("State").GetString() == "reserved",
            "Current authority denial leaves the uncertain persisted reservation closed and commits no entitlements");
        await ControlAsync(producer, new { command = "platform-role", value = "admin" });
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await CheckAsync(Organization(await ObserveAsync(producer), setup.Organization).GetProperty("PlanId").GetString() == claimedPlan,
            "A newly approved current operator resumes the same canonical global plan after the authority failure");
    }

    [Test]
    public async Task RoleRemovedAfterCapMarkerCannotProjectOwnerAlertUntilANewCurrentOperatorRetries()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_organizations", id = setup.Organization, after = true });
        await using var running = await StartAsync(scenario, Report(setup.Organization));
        try
        {
            await PausedAsync(producer);
            await ControlAsync(producer, new { command = "platform-role", value = "" });
        }
        finally { await ControlAsync(producer, new { command = "internal-release-put" }); }
        var rejected = await running.ResultAsync();
        await CheckAsync(rejected.Exit == 1 && rejected.Error == "report-internal-billing: Platform administrator authorization is required.\n" &&
            Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
            "A newly revoked operator cannot use its earlier cap reservation to append an owner notification");
        await ControlAsync(producer, new { command = "platform-role", value = "admin" });
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        await CheckAsync(Alerts(await ObserveAsync(producer), setup.Organization).Length == 1,
            "A real fresh operator retry safely resumes the reserved month alert once");
    }

    [Test]
    public async Task NativeCapChangeDuringPendingAlertRejectsStaleInboxProjectionAndLaterFreshReportRecovers()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_organizations", id = setup.Organization, after = true });
        await using var running = await StartAsync(scenario, Report(setup.Organization));
        try
        {
            await PausedAsync(producer);
            await CapAsync(browser, "200.00");
        }
        finally { await ControlAsync(producer, new { command = "internal-release-put" }); }
        var rejected = await running.ResultAsync();
        await CheckAsync(rejected.Exit == 1 && Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
            "The old report cannot append an owner inbox event after the native organization cap changes");
        var fresh = await RunAsync(scenario, Report(setup.Organization));
        await CheckAsync(fresh.Exit == 0 && Json(fresh).GetProperty("spendCapCents").GetInt64() == 20000 && !Json(fresh).GetProperty("capReached").GetBoolean() &&
            Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
            "A fresh actual report reads the new cap and does not deliver the old pending cap event");
        await CapAsync(browser, "120.00");
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        await CheckAsync(Alerts(await ObserveAsync(producer), setup.Organization).Length == 1,
            "A later coherent cap report recovers one current owner event after native cap restoration");
    }

    [Test]
    public async Task NativeCapChangeInsideFinalForcedRefreshRejectsTheStaleOwnerInboxWrite()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_organizations", id = setup.Organization, after = true });
        await using var running = await StartAsync(scenario, Report(setup.Organization));
        try
        {
            await PausedAsync(producer);
            await ControlAsync(producer, new { command = "pause-refresh" });
            await ControlAsync(producer, new { command = "internal-release-put" });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await ObserveAsync(producer)).GetProperty("refreshPauses").GetInt32() == 0)
                await Task.Delay(20, deadline.Token);
            // The report has already checked its owner/config/cap snapshot.
            // The native owner now changes the cap during the real producer's
            // final forced token rotation before the account inbox CAS.
            await CapAsync(browser, "200.00");
            await CheckAsync(Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
                "The actual paused refresh precedes every owner inbox write");
        }
        finally
        {
            await ControlAsync(producer, new { command = "release-refresh" });
            await ControlAsync(producer, new { command = "internal-release-put" });
        }
        var rejected = await running.ResultAsync();
        await CheckAsync(rejected.Exit == 1 && rejected.Error == "report-internal-billing: Internal billing changed while recording its cap alert.\n" &&
            Json(rejected).GetProperty("available").GetBoolean() &&
            Json(rejected).GetProperty("spendCapCents").GetInt64() == 12000 && Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
            "The command preserves its genuine measured bill but rejects stale alert delivery after a cap change inside refresh");
        var fresh = await RunAsync(scenario, Report(setup.Organization));
        await CheckAsync(fresh.Exit == 0 && fresh.Error == "" && Json(fresh).GetProperty("spendCapCents").GetInt64() == 20000 &&
            !Json(fresh).GetProperty("capReached").GetBoolean() && Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
            "The next full approved command uses the current cap without replaying the old owner alert");
        await ActiveAsync(browser);
        await NoStripeAsync(await ObserveAsync(producer));
    }

    [Test]
    public async Task AlreadyDeliveredMonthStillRevalidatesCurrentCapBeforeReportingDeduplicationSuccess()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        var delivered = Alerts(await ObserveAsync(producer), setup.Organization).Single();
        var pauses = (await ObserveAsync(producer)).GetProperty("refreshPauses").GetInt32();
        await using var report = await StartAsync(scenario, Report(setup.Organization),
            () => ControlAsync(producer, new { command = "pause-refresh-after", count = 1 }));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await ObserveAsync(producer)).GetProperty("refreshPauses").GetInt32() == pauses)
                await Task.Delay(20, deadline.Token);
            await CapAsync(browser, "200.00");
        }
        finally { await ControlAsync(producer, new { command = "release-refresh" }); }
        var stale = await report.ResultAsync();
        var observed = await ObserveAsync(producer);
        await CheckAsync(stale.Exit == 1 && stale.Error == "report-internal-billing: Internal billing changed while recording its cap alert.\n" &&
            Json(stale).GetProperty("available").GetBoolean() && Json(stale).GetProperty("spendCapCents").GetInt64() == 12000 &&
            Alerts(observed, setup.Organization).Single().GetRawText() == delivered.GetRawText() &&
            Organization(observed, setup.Organization).GetProperty("SpendCapCents").GetInt64() == 20000,
            "An existing delivered month never bypasses current signed refresh and canonical cap validation or falsely reports deduplication success for a stale bill");
        await CapAsync(browser, "120.00");
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        await CheckAsync(Alerts(await ObserveAsync(producer), setup.Organization).Single().GetRawText() == delivered.GetRawText(),
            "A fresh approved report with the restored current cap succeeds without rewriting or duplicating the original delivered notification");
        await ActiveAsync(browser);
        await NoStripeAsync(await ObserveAsync(producer));
    }

    [Test]
    public async Task WrongProducerOrganizationDocumentKeyRejectsNativeContextSpendingAndCommandWrites()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var csrf = await HttpJourney.CsrfAsync(browser, "/usage?tab=settings");
        JsonElement before = default;
        var rejected = await RunAsync(scenario, Configure(setup.Organization, "20000"), async () =>
        {
            before = await ObserveAsync(producer);
            await ControlAsync(producer, new { command = "internal-misbind-organization-read", id = setup.Organization });
        });
        try
        {
            await CheckAsync(rejected.Exit == 1 && rejected.Error == "configure-internal-billing: Internal billing dependencies are unavailable.\n" &&
                !rejected.Output.Contains("configured report-only", StringComparison.Ordinal),
                "The full public command fails closed when the producer key differs from the requested organization despite a correct payload ID");
            using var context = await browser.GetAsync("/api/v1/dashboard/context");
            await CheckAsync(context.StatusCode == HttpStatusCode.ServiceUnavailable,
                "Native account context rejects a well-formed organization payload returned under a foreign producer document key");
            using var spending = await HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/usage/internal-spending-cap", new()
            { ["_csrf"] = csrf, ["spendCap"] = "200.00" });
            await CheckAsync(spending.StatusCode == HttpStatusCode.ServiceUnavailable,
                "The actual spending form cannot authorize or write through a misbound organization read");
            var after = await ObserveAsync(producer);
            await CheckAsync(after.GetProperty("billing").GetProperty("misboundOrganizationReads").GetInt32() >= 3 &&
                after.GetProperty("billing").GetProperty("writes").GetArrayLength() == before.GetProperty("billing").GetProperty("writes").GetArrayLength() &&
                Organization(after, setup.Organization).GetProperty("Id").GetString() == setup.Organization &&
                Organization(after, setup.Organization).GetProperty("SpendCapCents").GetInt64() == 12000 &&
                Organization(after, setup.Organization).GetProperty("InternalBilling").GetRawText() == Organization(before, setup.Organization).GetProperty("InternalBilling").GetRawText(),
                "Only the external read envelope is corrupted; no canonical organization, account, policy or payment fence write is admitted");
            await NoStripeAsync(after);
        }
        finally { await ControlAsync(producer, new { command = "internal-clear-faults" }); }
        await ActiveAsync(browser);
    }

    private static async Task<HttpResponseMessage> CheckoutAsync(HttpClient browser, string? csrf = null)
    {
        csrf ??= await HttpJourney.CsrfAsync(browser, "/profile");
        return await HttpJourney.PostFormAsync(browser, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly", ["return"] = "/organization?tab=billing" });
    }

    private static async Task CapAsync(HttpClient browser, string value)
    {
        var csrf = await HttpJourney.CsrfAsync(browser, "/usage?tab=settings");
        using var response = await HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/usage/internal-spending-cap", new()
        { ["_csrf"] = csrf, ["spendCap"] = value });
        await CheckAsync(response.StatusCode == HttpStatusCode.SeeOther && response.Headers.Location?.OriginalString == "/usage?tab=settings&saved=ok",
            "The actual native owner spending form commits the organization cap");
    }

    private static async Task PausedAsync(HttpClient producer, int count = 1, string counter = "pausedWrites", Task<HttpResponseMessage>? checkout = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!deadline.IsCancellationRequested)
        {
            if ((await ObserveAsync(producer)).GetProperty("billing").GetProperty(counter).GetInt32() >= count) return;
            if (checkout is { IsCompleted: true })
            {
                using var response = await checkout;
                await CheckAsync(false, $"Checkout returned {(int)response.StatusCode} before reaching its persistence barrier: {await response.Content.ReadAsStringAsync()}");
            }
            await Task.Delay(20, deadline.Token);
        }
        await CheckAsync(false, "Actual app writes reach the external producer barrier before the race is released");
    }

    [Test]
    public async Task ConcurrentNativeReportsRecoverOneOwnerMonthNotificationThroughAccountCas()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_accounts", count = 2 });
        await using var first = await StartAsync(scenario, Report(setup.Organization));
        await using var second = await StartAsync(scenario, Report(setup.Organization));
        try { await PausedAsync(producer, 2); }
        finally { await ControlAsync(producer, new { command = "internal-release-put" }); }
        await BillAsync(await first.ResultAsync(), setup.Month); await BillAsync(await second.ResultAsync(), setup.Month);
        await CheckAsync(Alerts(await ObserveAsync(producer), setup.Organization).Length == 1,
            "Concurrent real public commands reconcile the same owner-month marker through conditional inbox writes");
    }

    [Test]
    public async Task MonthlyProducerFanoutCrossingUtcBoundaryKeepsTheCapturedMonthAndItsAlertIdentity()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var utcNow = DateTimeOffset.UtcNow;
        var nextMonth = new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var boundaryStart = nextMonth.AddMinutes(-10);
        var setup = await PrepareAsync(browser, producer, time: boundaryStart);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        await ControlAsync(producer, new { command = "internal-pause-usage" });
        await using var report = await StartAsync(scenario, Report(setup.Organization));
        try
        {
            await PausedAsync(producer, 4, "pausedUsage");
            await ControlAsync(producer, new { command = "advance", seconds = 600 });
        }
        finally { await ControlAsync(producer, new { command = "internal-release-usage" }); }
        var result = await report.ResultAsync();
        await BillAsync(result, setup.Month);
        await CheckAsync(Json(result).GetProperty("periodEnd").GetDateTimeOffset() == setup.Month.AddMonths(1) &&
            Alerts(await ObserveAsync(producer), setup.Organization).Single().GetProperty("BillingMonth").GetString() == setup.Month.ToString("yyyy-MM"),
            "Actual usage replies straddling UTC midnight remain bound to the original calendar month and deduplication key");
        await ScopeAsync(await ObserveAsync(producer), setup.Organization, setup.Projects, setup.Month);
    }

    [Test]
    public async Task NativePatCurrentMcpReadsExposeCopiedInternalContractWithoutPaymentAuthorityOrAlertSideEffects()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var secret = await HttpJourney.CreatePatAsync(browser, "Internal policy protocol journey");
        var billing = await ToolAsync(browser, secret, "get_billing", new { organizationId = setup.Organization });
        await CheckAsync(billing.GetProperty("planKey").GetString() == "internal" && billing.GetProperty("planName").GetString() == "TerraScale Internal" &&
            billing.GetProperty("canManage").GetBoolean() && !billing.GetProperty("hasSubscription").GetBoolean() &&
            billing.GetProperty("paymentMethod").GetProperty("validated").ValueKind == JsonValueKind.False &&
            billing.GetProperty("paymentMethod").EnumerateObject().Count() == 1,
            "Original MCP retains the owner's management scope and an unvalidated payment-method receipt without inventing a card or Stripe subscription");
        var plans = await ToolAsync(browser, secret, "list_plans", new { organizationId = setup.Organization });
        await CheckAsync(plans.GetProperty("currentPlanKey").GetString() == "internal" && plans.GetProperty("currentInterval").GetString() == "monthly" &&
            plans.GetProperty("plans").EnumerateArray().All(row => row.GetProperty("key").GetString() != "internal"),
            "The source-compatible public plan catalog retains its hidden current monthly contract without publishing the private plan");
        var usage = await ToolAsync(browser, secret, "get_usage", new { organizationId = setup.Organization });
        await CheckAsync(usage.GetProperty("internalBill").GetProperty("available").GetBoolean() &&
            usage.GetProperty("internalBill").GetProperty("totalCents").GetInt64() == 12440 &&
            Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
            "Read-only current-protocol MCP estimates real monthly usage without appending owner notifications");
        var discovery = await HttpJourney.McpAsync(browser, secret, "tools/list", new { });
        await CheckAsync(!discovery.GetProperty("result").GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "set_spending_controls"),
            "The obsolete public spending setter is absent even for an internal organization; its report-only cap remains a native internal control");
        var checkout = await HttpJourney.McpAsync(browser, secret, "tools/call", new
        { name = "create_checkout", arguments = new { organizationId = setup.Organization, plan = "pro", interval = "monthly" } });
        await CheckAsync(checkout.GetProperty("result").GetProperty("isError").GetBoolean(),
            "Current MCP cannot create a Stripe checkout for the internal organization");
        await NoStripeAsync(await ObserveAsync(producer));
        var tokens = await ToolAsync(browser, secret, "list_access_tokens", new { });
        var tokenId = tokens.GetProperty("accessTokens").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "Internal policy protocol journey").GetProperty("id").GetString();
        var revoked = await ToolAsync(browser, secret, "revoke_access_token", new { tokenId });
        await CheckAsync(revoked.GetProperty("revoked").GetBoolean(),
            "The real personal token is revoked through its public owning MCP tool after the journey");
    }

    [Test]
    public async Task WholeCliApplicationConstructionFailureIsSanitizedBeforeDeviceIssueAndPreservesBrowser()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var setup = await PrepareAsync(browser, producer);
        await ControlAsync(producer, new { command = "internal-fail-cli-build-once" });
        using var output = new StringWriter(); using var error = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exit = await scenario.RunCliAsync(Report(setup.Organization), output, error, deadline.Token);
        await CheckAsync(exit == 1 && output.ToString() == "" && error.ToString() == "report-internal-billing: Internal billing dependencies are unavailable.\n" &&
            scenario.CliRequests.Count == 0,
            "The actual public entry sanitizes a whole application construction failure before issuing any device grant");
        var observed = await ObserveAsync(producer);
        await CheckAsync(observed.GetProperty("billing").GetProperty("cliBuildFailures").GetInt32() == 1 &&
            observed.GetProperty("billing").GetProperty("bindings").GetArrayLength() == 0 &&
            observed.GetProperty("billing").GetProperty("singleton").ValueKind == JsonValueKind.Null,
            "The failed real command graph leaves no payment fence or internal singleton");
        await ActiveAsync(browser);
        await NoStripeAsync(observed);
    }

    [Test]
    [Arguments("after-marker")]
    [Arguments("inside-refresh")]
    public async Task NativelyEnrolledNomineeTakesOwnershipBeforeOldReportDeliveryAndOnlyCurrentOwnerGetsTheMonthAlert(string boundary)
    {
        await using var scenario = await AuthProviderScenario.StartOwnershipAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var owner = scenario.CreateClient();
        using var nominee = scenario.CreateClient(replica: true);
        var setup = await PrepareAsync(owner, producer);
        await SuccessAsync(await RunAsync(scenario, Configure(setup.Organization)), setup.Organization);
        var enrolled = await EnrollNomineeAsync(nominee, producer);
        await InviteAndJoinAsync(owner, nominee, enrolled.Email);
        await ControlAsync(producer, new { command = "internal-pause-put", collection = "dashboard_organizations", id = setup.Organization, after = true });
        await using var report = await StartAsync(scenario, Report(setup.Organization));
        try
        {
            await PausedAsync(producer);
            if (boundary == "inside-refresh")
            {
                var pauses = (await ObserveAsync(producer)).GetProperty("refreshPauses").GetInt32();
                await ControlAsync(producer, new { command = "pause-refresh" });
                await ControlAsync(producer, new { command = "internal-release-put" });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while ((await ObserveAsync(producer)).GetProperty("refreshPauses").GetInt32() == pauses)
                    await Task.Delay(20, deadline.Token);
            }
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=members");
            using var started = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/members/ownership", new()
            { ["_csrf"] = csrf, ["userID"] = enrolled.Subject });
            await CheckAsync(started.StatusCode == HttpStatusCode.SeeOther,
                "The original owner offers ownership to the genuinely enrolled and invited active member through the native form");
            using var wrongActor = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/members/ownership/confirm", new()
            { ["_csrf"] = csrf });
            var offered = (await OwnershipProbeAsync(producer)).GetProperty("organizations").EnumerateArray().Single(row => row.GetProperty("id").GetString() == setup.Organization);
            await CheckAsync(wrongActor.StatusCode == HttpStatusCode.Forbidden &&
                offered.GetProperty("pendingTransfer").GetProperty("toUserId").GetString() == enrolled.Subject &&
                offered.GetProperty("members").EnumerateArray().Single(row => row.GetProperty("userId").GetString() == enrolled.Subject).GetProperty("role").GetString() == "member",
                "The old owner is actually refused by the native SDK confirm operation and cannot consume the nominee's pending offer or change its member role");
            var nomineeCsrf = await HttpJourney.CsrfAsync(nominee, "/organization?tab=members");
            using var confirmed = await HttpJourney.PostFormAsync(nominee, "/api/v1/dashboard/organization/members/ownership/confirm", new()
            { ["_csrf"] = nomineeCsrf });
            await CheckAsync(confirmed.StatusCode == HttpStatusCode.SeeOther,
                "Only the nominee confirms the real SDK ownership offer through its independent native session on the other replica");
        }
        finally
        {
            await ControlAsync(producer, new { command = "release-refresh" });
            await ControlAsync(producer, new { command = "internal-release-put" });
        }
        var stale = await report.ResultAsync();
        await CheckAsync(stale.Exit == 1 && stale.Error == "report-internal-billing: Internal billing changed while recording its cap alert.\n" &&
            Json(stale).GetProperty("available").GetBoolean() && Alerts(await ObserveAsync(producer), setup.Organization).Length == 0,
            "An old owner snapshot cannot project an inbox alert after a native provider ownership change at the actual pending-write or refresh boundary");
        var oldIdentity = await IdentityAsync(owner);
        var newIdentity = await IdentityAsync(nominee);
        await CheckAsync(oldIdentity.GetProperty("role").GetString() == "admin" && newIdentity.GetProperty("role").GetString() == "owner" &&
            newIdentity.GetProperty("organizationId").GetString() == setup.Organization && newIdentity.GetProperty("userId").GetString() == enrolled.Subject,
            "Fresh native contexts match the ts-auth contract: nominee becomes owner and starter becomes admin");
        using var operatorDenied = await nominee.GetAsync("/admin/users");
        await CheckAsync(operatorDenied.StatusCode == HttpStatusCode.Forbidden &&
            (await ObserveAsync(producer)).GetProperty("issuedAdminRoleGrantsByUser").GetProperty(enrolled.Subject).GetInt32() == 0,
            "The newly promoted organization owner still has no signed platform operator grant after another real forced refresh");
        var probe = await OwnershipProbeAsync(producer);
        var transferred = probe.GetProperty("organizations").EnumerateArray().Single(row => row.GetProperty("id").GetString() == setup.Organization);
        await CheckAsync(probe.GetProperty("invitationCreates").GetInt32() == 1 && probe.GetProperty("invitationAccepts").GetInt32() == 1 &&
            probe.GetProperty("transferStarts").GetInt32() == 1 && probe.GetProperty("transferConfirms").GetInt32() == 1 &&
            probe.GetProperty("failures").GetArrayLength() == 0 && probe.GetProperty("acceptances").EnumerateArray().Single().GetProperty("email").GetString() == enrolled.Email &&
            transferred.GetProperty("pendingTransfer").ValueKind == JsonValueKind.Null &&
            transferred.GetProperty("members").EnumerateArray().Single(row => row.GetProperty("userId").GetString() == enrolled.Subject).GetProperty("role").GetString() == "owner" &&
            transferred.GetProperty("members").EnumerateArray().Single(row => row.GetProperty("userId").GetString() == oldIdentity.GetProperty("userId").GetString()).GetProperty("role").GetString() == "admin" &&
            probe.GetProperty("requests").EnumerateArray().Any(row => row.GetProperty("path").GetString() == "/v1/terrascale/organizations/" + setup.Organization + "/ownership-transfer/confirm" &&
                row.GetProperty("userId").GetString() == enrolled.Subject),
            "External producer evidence proves the verified email acceptance and nominee-authorized SDK confirm without any dashboard projection shortcut");
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        await BillAsync(await RunAsync(scenario, Report(setup.Organization)), setup.Month);
        using var oldInbox = await owner.GetAsync("/notifications");
        using var newInbox = await nominee.GetAsync("/notifications");
        await CheckAsync(oldInbox.StatusCode == HttpStatusCode.OK && newInbox.StatusCode == HttpStatusCode.OK &&
            !(await oldInbox.Content.ReadAsStringAsync()).Contains(CapAlert, StringComparison.Ordinal) &&
            (await newInbox.Content.ReadAsStringAsync()).Contains(CapAlert, StringComparison.Ordinal) &&
            Alerts(await ObserveAsync(producer), setup.Organization).Length == 1,
            "A fresh actual platform operator reports twice but delivers exactly one original month alert to the actual current owner and none to the previous owner");
        await NoStripeAsync(await ObserveAsync(producer));
        await PolicyAsync(Organization(await ObserveAsync(producer), setup.Organization));
        var logoutCsrf = await HttpJourney.CsrfAsync(nominee, "/profile");
        using var ended = await HttpJourney.PostFormAsync(nominee, "/api/v1/auth/logout", new() { ["_csrf"] = logoutCsrf });
        await CheckAsync(ended.StatusCode == HttpStatusCode.SeeOther,
            "The independently enrolled owner signs out through the actual owning session endpoint");
        await ActiveAsync(owner);
        using var signedBackIn = await HttpJourney.LoginAsync(nominee, enrolled.Email.ToUpperInvariant(), "native-second-owner-123", "/api/v1/auth/me");
        await CheckAsync(signedBackIn.StatusCode == HttpStatusCode.SeeOther && (await IdentityAsync(nominee)).GetProperty("userId").GetString() == enrolled.Subject,
            "The same private producer user signs back in with its own password through normal case-insensitive email admission, independently of the operator's family");
    }

    private static async Task<(string Subject, string Email)> EnrollNomineeAsync(HttpClient browser, HttpClient producer)
    {
        const string email = "second-owner@example.com";
        const string password = "native-second-owner-123";
        using (var verified = await HttpJourney.LoginAsync(browser, email, password, "/onboarding", signUp: true))
            await CheckAsync(verified.StatusCode == HttpStatusCode.SeeOther && verified.Headers.Location?.OriginalString == "/onboarding",
                $"Hosted ts-auth sign-up returned HTTP {(int)verified.StatusCode}, Location '{verified.Headers.Location?.OriginalString ?? "<none>"}'");
        foreach (var fields in new Dictionary<string, string>[]
        {
            new() { ["step"] = "account-information", ["displayName"] = "Second Owner" },
            new() { ["step"] = "payment-method", ["bypass"] = "1" },
            new() { ["step"] = "project", ["name"] = "Personal", ["region"] = "GRU" }
        })
        {
            fields["_csrf"] = await HttpJourney.CsrfAsync(browser, "/onboarding");
            using var saved = await HttpJourney.PostFormAsync(browser, "/onboarding/step", fields);
            await CheckAsync(saved.StatusCode == HttpStatusCode.SeeOther,
                "The second verified account completes each genuine native Personal onboarding step");
        }
        var identity = await IdentityAsync(browser);
        var subject = identity.GetProperty("userId").GetString()!;
        await CheckAsync(identity.GetProperty("email").GetString() == email && identity.GetProperty("onboardingCompleted").GetBoolean(),
            "Native identity exposes the genuinely verified second account and persisted completed onboarding");
        using var denied = await browser.GetAsync("/admin/users");
        await CheckAsync(denied.StatusCode == HttpStatusCode.Forbidden && (await denied.Content.ReadAsStringAsync()).Contains("Your account does not have platform admin access.", StringComparison.Ordinal),
            "The nominee's own forced signed refresh carries no platform role and cannot turn organization ownership into operator authority");
        var observed = await ObserveAsync(producer);
        var principal = observed.GetProperty("registeredPrincipals").EnumerateArray().Single(row => row.GetProperty("userId").GetString() == subject);
        await CheckAsync(principal.GetProperty("email").GetString() == email && principal.GetProperty("platformRole").ValueKind == JsonValueKind.Null &&
            observed.GetProperty("contractFailures").GetInt32() == 0,
            "Only hosted sign-up admitted the second private producer row; no role fixture control or privileged management route granted it authority");
        return (subject, email);
    }

    private static async Task InviteAndJoinAsync(HttpClient owner, HttpClient invited, string email)
    {
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=members");
        using var invitation = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/members/invite", new()
        { ["_csrf"] = csrf, ["email"] = email, ["role"] = "member" });
        var html = await invitation.Content.ReadAsStringAsync();
        await CheckAsync(invitation.StatusCode == HttpStatusCode.OK, "The current owner issues an actual signed scoped SDK member invitation through the native dashboard form");
        var link = WebUtility.HtmlDecode(Regex.Match(html, "id=\"invitation-link\"[^>]*href=\"([^\"]+)\"").Groups[1].Value);
        await CheckAsync(Uri.TryCreate(link, UriKind.Absolute, out var target) && target.AbsolutePath.StartsWith("/invitations/", StringComparison.Ordinal),
            "The producer invitation token is delivered exactly once by the native invitation link");
        var path = new Uri(link).AbsolutePath;
        var inviteCsrf = await HttpJourney.CsrfAsync(invited, path);
        using var accepted = await HttpJourney.PostFormAsync(invited, path + "/respond", new()
        { ["_csrf"] = inviteCsrf, ["action"] = "accept" });
        await CheckAsync(accepted.StatusCode == HttpStatusCode.SeeOther && accepted.Headers.Location?.OriginalString == "/organization?tab=members&invitation=accepted",
            "The separately verified invited email accepts through the native form and joins only after actual producer membership reread");
    }

    private static async Task<JsonElement> IdentityAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/api/v1/auth/me");
        await CheckAsync(response.StatusCode == HttpStatusCode.OK, "The native session resolves its own provider-bound persisted identity");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
    }

    private static async Task<JsonElement> OwnershipProbeAsync(HttpClient producer)
    {
        using var response = await producer.GetAsync("/__test/ownership-sdk/probe"); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
    }

    private static async Task<JsonElement> ToolAsync(HttpClient browser, string secret, string name, object arguments)
    {
        var response = await HttpJourney.McpAsync(browser, secret, "tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        await CheckAsync(!result.TryGetProperty("isError", out var failure) || !failure.GetBoolean(),
            "The actual current-protocol MCP tool succeeds for its fresh authorized native PAT");
        return result.GetProperty("structuredContent");
    }

    private static async Task<Setup> PrepareAsync(HttpClient browser, HttpClient producer, int organizations = 1, DateTimeOffset? time = null, bool paymentMethodValidated = false)
    {
        var scenarioTime = (time ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var month = new DateTimeOffset(scenarioTime.Year, scenarioTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        await ControlAsync(producer, new { command = "set-time", value = scenarioTime });
        await ControlAsync(producer, new { command = "platform-role", value = "admin" });
        using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        await CheckAsync(login.StatusCode == HttpStatusCode.SeeOther, "The operator browser obtains a real signed first-party native session");
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];
        // A cookie-less client receives its own CSRF nonce cookie, bound to this session.
        using var probe = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = browser.BaseAddress };
        probe.DefaultRequestHeaders.Add("Cookie", cookie);
        using var page = await probe.GetAsync("/profile");
        var nonce = page.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-ts-dashboard-csrf=", StringComparison.Ordinal)).Split(';')[0];
        var csrf = WebUtility.HtmlDecode(Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value);
        using var response = await producer.PostAsJsonAsync("/__test/control", new { command = "internal-seed", count = organizations, paymentMethodValidated });
        await CheckAsync(response.IsSuccessStatusCode, "The external producer prepares the pre-existing canonical service organization");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var targets = json.RootElement.GetProperty("targets").EnumerateArray().ToArray();
        return new(targets[0].GetProperty("organizationId").GetString()!, targets[0].GetProperty("projects").EnumerateArray().Select(row => row.GetProperty("id").GetString()!).ToArray(),
            cookie + "; " + nonce, targets.Select(row => row.GetProperty("organizationId").GetString()!).ToArray(), month);
    }

    private static HttpClient CookieClient(AuthProviderScenario scenario, string cookie, bool replica = false)
    {
        var client = scenario.CreateClient(replica);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static string[] Configure(string organization, string cap = "12000", string writeRate = "100") =>
        ["configure-internal-billing", "--org", organization, "--admin-email", "user@example.com", "--base-plan", "pro", "--spend-cap-cents", cap, "--write-cents-per-million", writeRate, "--storage-cents-per-gb-month", "20"];
    private static string[] Report(string organization) => ["report-internal-billing", "--org", organization, "--admin-email", "user@example.com"];

    private static async Task<Command> RunAsync(AuthProviderScenario scenario, string[] args, Func<Task>? beforeDecision = null)
    {
        await using var running = await StartAsync(scenario, args, beforeDecision);
        return await running.ResultAsync();
    }

    // Approves the printed code on the test stand-in for ts-auth's hosted device verification page.
    private static async Task<RunningCommand> StartAsync(AuthProviderScenario scenario, string[] args, Func<Task>? beforeDecision = null)
    {
        var running = new RunningCommand();
        running.Run = scenario.RunCliAsync(args, running.Output, running.Error, running.Deadline.Token);
        try
        {
            var approval = new Uri(await running.Output.Prompt.WaitAsync(running.Deadline.Token));
            await CheckAsync(approval.Scheme == "https" && approval.IsDefaultPort && approval.Host == "auth.terrascale.tech" &&
                approval.AbsolutePath == "/ui/terrascale/device" && approval.Query.StartsWith("?user_code=", StringComparison.Ordinal),
                "The public command prints ts-auth's hosted device verification page");
            var code = Uri.UnescapeDataString(approval.Query["?user_code=".Length..]);
            if (beforeDecision is not null) await beforeDecision();
            await CheckAsync(scenario.DecideDevice(code), "The operator approves the command's pending ts-auth device grant");
            return running;
        }
        catch { await running.DisposeAsync(); throw; }
    }

    private static JsonElement Json(Command result)
    {
        var first = result.Output.IndexOf('\n');
        var second = result.Output.IndexOf('\n', first + 1);
        using var json = JsonDocument.Parse(result.Output[(second + 1)..]);
        return json.RootElement.Clone();
    }

    private static async Task SuccessAsync(Command result, string organization) => await CheckAsync(result.Exit == 0 && result.Error == "" &&
        result.Output.EndsWith($"configured report-only internal billing for organization {organization} using base plan pro\n", StringComparison.Ordinal),
        "The actual public configure entry completes with the original report-only receipt");

    private static async Task BillAsync(Command result, DateTimeOffset month)
    {
        var bill = Json(result);
        await CheckAsync(result.Exit == 0 && result.Error == "" && bill.GetProperty("available").GetBoolean() && bill.GetProperty("reportOnly").GetBoolean() &&
            bill.GetProperty("periodStart").GetDateTimeOffset() == month &&
            bill.GetProperty("periodEnd").GetDateTimeOffset().Offset == TimeSpan.Zero &&
            bill.GetProperty("reads").GetInt64() == 123_456 && bill.GetProperty("writes").GetInt64() == 106_000_000 &&
            bill.GetProperty("logicalBytes").GetInt64() == 12_000_000_000L &&
            bill.GetProperty("baseCents").GetInt64() == 2000 && bill.GetProperty("writeCents").GetInt64() == 10400 &&
            bill.GetProperty("storageCents").GetInt64() == 40 && bill.GetProperty("totalCents").GetInt64() == 12440 &&
            bill.GetProperty("spendCapCents").GetInt64() == 12000 && bill.GetProperty("capReached").GetBoolean() && bill.GetProperty("alert").GetString() == CapAlert,
            "The actual CLI reports exact measured UTC-month counters and copied-plan overage charges without creating a Stripe invoice");
    }

    private static async Task UnknownMeteringAsync(JsonElement bill) => await CheckAsync(
        bill.GetProperty("periodStart").GetDateTimeOffset() == default && bill.GetProperty("periodEnd").GetDateTimeOffset() == default &&
        bill.GetProperty("reads").GetInt64() == 0 && bill.GetProperty("writes").GetInt64() == 0 && bill.GetProperty("logicalBytes").GetInt64() == 0 &&
        bill.GetProperty("baseCents").GetInt64() == 2000 && bill.GetProperty("writeCents").GetInt64() == 0 &&
        bill.GetProperty("storageCents").GetInt64() == 0 && bill.GetProperty("totalCents").GetInt64() == 0 &&
        !bill.TryGetProperty("alert", out _),
        "An incomplete or overflowing metering aggregate publishes no invented period, partial counters, variable charges or alert; the copied base contract remains known");

    private static async Task CoherentPricingFailureAsync(JsonElement bill, DateTimeOffset month) => await CheckAsync(
        !bill.GetProperty("available").GetBoolean() && bill.GetProperty("reportOnly").GetBoolean() && !bill.GetProperty("capReached").GetBoolean() &&
        bill.GetProperty("error").GetString() == InvalidAmount && bill.GetProperty("periodStart").GetDateTimeOffset() == month &&
        bill.GetProperty("periodEnd").GetDateTimeOffset() > bill.GetProperty("periodStart").GetDateTimeOffset() &&
        bill.GetProperty("reads").GetInt64() == 123456 && bill.GetProperty("writes").GetInt64() == 106000000 &&
        bill.GetProperty("logicalBytes").GetInt64() == 12_000_000_000L && bill.GetProperty("baseCents").GetInt64() == 2000 &&
        bill.GetProperty("writeCents").GetInt64() == 0 && bill.GetProperty("storageCents").GetInt64() == 0 && bill.GetProperty("totalCents").GetInt64() == 0 &&
        bill.GetProperty("writeCentsPerMillion").GetInt64() == long.MaxValue && !bill.TryGetProperty("alert", out _),
        "Pricing overflow preserves the actual coherent UTC-month metering and valid configured rate without publishing an amount or cap alert");

    private static async Task PolicyAsync(JsonElement organization)
    {
        var policy = organization.GetProperty("InternalBilling");
        await CheckAsync(policy.ValueKind == JsonValueKind.Object && organization.GetProperty("PlanId").GetString() == policy.GetProperty("PlanId").GetString() &&
            policy.GetProperty("PlanId").GetString() != "7CNW2H87JE68ATZX224CV2G625" &&
            policy.GetProperty("Plan").GetProperty("Key").GetString() == "internal" && policy.GetProperty("Plan").GetProperty("PriceCents").GetInt64() == 2000 &&
            policy.GetProperty("Allowances").GetProperty("MonthlyOperations").GetInt64() == 2_000_000 &&
            policy.GetProperty("Allowances").GetProperty("StorageBytes").GetInt64() == 10_000_000_000L &&
            organization.GetProperty("BillingStatus").GetString() == "active" && organization.GetProperty("PaidExcess").GetBoolean() &&
            organization.GetProperty("Projects").EnumerateObject().All(row => row.Value.GetProperty("Status").GetString() == "active"),
            "The running command persists the copied paid contract under its distinct hidden identity while services remain active");
    }

    private static JsonElement Organization(JsonElement observed, string organization) => observed.GetProperty("billing").GetProperty("organizations").EnumerateArray()
        .Single(row => row.GetProperty("id").GetString() == organization).GetProperty("document");
    private static JsonElement[] Alerts(JsonElement observed, string organization) => observed.GetProperty("billing").GetProperty("notifications").EnumerateArray()
        .Where(row => row.GetProperty("OrganizationId").GetString() == organization && row.GetProperty("Type").GetString() == "internal-cap").ToArray();
    private static async Task NoStripeAsync(JsonElement observed) => await CheckAsync(observed.GetProperty("billing").GetProperty("stripeRequests").GetArrayLength() == 0,
        "Internal configuration and reporting do not contact Stripe or create payment resources");
    private static async Task ActiveAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/api/v1/auth/me");
        await CheckAsync(response.StatusCode == HttpStatusCode.OK, "Report-only cap enforcement preserves the independent browser and active services");
    }
    private static async Task ScopeAsync(JsonElement observed, string organization, string[] projects, DateTimeOffset month)
    {
        var rows = observed.GetProperty("billing").GetProperty("usageRequests").EnumerateArray().ToArray();
        await CheckAsync(projects.Length > 0 && rows.Length >= projects.Length && rows.All(row => row.GetProperty("organizationId").GetString() == organization &&
            projects.Contains(row.GetProperty("projectId").GetString()) && row.GetProperty("userId").GetString() == observed.GetProperty("subject").GetString() &&
            row.GetProperty("from").GetDateTimeOffset() == month) &&
            rows.Select(row => row.GetProperty("projectId").GetString()).Distinct().Count() == projects.Length,
            "Actual producer GETs meter every current service project under the approved principal and exact UTC month");
    }
    private static async Task<JsonElement> ObserveAsync(HttpClient producer)
    {
        using var response = await producer.GetAsync("/__test/observations"); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
    }
    private static async Task ControlAsync(HttpClient producer, object command)
    { using var response = await producer.PostAsJsonAsync("/__test/control", command); response.EnsureSuccessStatusCode(); }
    private static async Task CheckAsync(bool value, string reason) => await Assert.That(value).IsTrue().Because(reason);
    private sealed record Setup(string Organization, string[] Projects, string Cookie, string[] Targets, DateTimeOffset Month);
    private sealed record Command(int Exit, string Output, string Error);
    private sealed class RunningCommand : IAsyncDisposable
    {
        public PromptWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public CancellationTokenSource Deadline { get; } = new(TimeSpan.FromSeconds(45));
        public Task<int> Run { get; set; } = null!;
        public async Task<Command> ResultAsync()
        {
            var exit = await Run;
            var text = Output.Snapshot;
            await CheckAsync(!(text + Error).Contains("tspat_", StringComparison.Ordinal) && !(text + Error).Contains("test-password", StringComparison.Ordinal) &&
                !(text + Error).Contains("device_code", StringComparison.Ordinal), "Command output contains its public receipt and never authentication secrets");
            return new(exit, text, Error.ToString());
        }
        public async ValueTask DisposeAsync()
        {
            if (Run is not null && !Run.IsCompleted) { await Deadline.CancelAsync(); await Run.WaitAsync(TimeSpan.FromSeconds(35)); }
            Deadline.Dispose(); Output.Dispose(); Error.Dispose();
        }
    }
    private sealed class PromptWriter : StringWriter
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource<string> prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Prompt => prompt.Task;
        public string Snapshot { get { lock (gate) return ToString(); } }
        public override void WriteLine(string? value)
        {
            lock (gate)
            {
                base.WriteLine(value);
                const string prefix = "Approve CLI access: ";
                if (value?.StartsWith(prefix, StringComparison.Ordinal) == true) prompt.TrySetResult(value[prefix.Length..]);
            }
        }
    }
}
