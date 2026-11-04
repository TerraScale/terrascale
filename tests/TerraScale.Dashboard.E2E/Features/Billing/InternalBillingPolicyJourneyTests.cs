using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardRootFixture>]
    [Category("Slow")]
    public sealed class InternalBillingPolicyJourneyTests(DashboardRootFixture fixture)
    {
        private const string CapAlert = "Internal spending cap reached: estimated bill $124.40, cap $120.00. Report only; services remain active.";
        private const string MissingUsage = "Every project's monthly usage is required to compute the internal bill and evaluate its cap.";
        private const string InvalidAmount = "The internal bill exceeds the supported amount or has invalid reporting rates.";

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
                if ((await ObserveAsync(producer)).GetProperty("billing").GetProperty(counter).GetInt32() >= count)
                {
                    return;
                }

                if (checkout is { IsCompleted: true })
                {
                    using var response = await checkout;
                    await CheckAsync(false, $"Checkout returned {(int)response.StatusCode} before reaching its persistence barrier: {await response.Content.ReadAsStringAsync()}");
                }
                await Task.Delay(20, deadline.Token);
            }
            await CheckAsync(false, "Actual app writes reach the external producer barrier before the race is released");
        }

        private static async Task<(string Subject, string Email)> EnrollNomineeAsync(HttpClient browser, HttpClient producer)
        {
            const string email = "second-owner@example.com";
            const string password = "native-second-owner-123";
            using (var verified = await HttpJourney.LoginAsync(browser, email, password, "/onboarding", signUp: true))
            {
                await CheckAsync(verified.StatusCode == HttpStatusCode.SeeOther && verified.Headers.Location?.OriginalString == "/onboarding",
                    $"Hosted ts-auth sign-up returned HTTP {(int)verified.StatusCode}, Location '{verified.Headers.Location?.OriginalString ?? "<none>"}'");
            }

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

        private static string[] Configure(string organization, string cap = "12000", string writeRate = "100")
        {
            return ["configure-internal-billing", "--org", organization, "--admin-email", "user@example.com", "--base-plan", "pro", "--spend-cap-cents", cap, "--write-cents-per-million", writeRate, "--storage-cents-per-gb-month", "20"];
        }

        private static string[] Report(string organization)
        {
            return ["report-internal-billing", "--org", organization, "--admin-email", "user@example.com"];
        }

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
                if (beforeDecision is not null)
                {
                    await beforeDecision();
                }

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

        private static async Task SuccessAsync(Command result, string organization)
        {
            await CheckAsync(result.Exit == 0 && result.Error == "" &&
            result.Output.EndsWith($"configured report-only internal billing for organization {organization} using base plan pro\n", StringComparison.Ordinal),
            "The actual public configure entry completes with the original report-only receipt");
        }

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

        private static async Task UnknownMeteringAsync(JsonElement bill)
        {
            await CheckAsync(
            bill.GetProperty("periodStart").GetDateTimeOffset() == default && bill.GetProperty("periodEnd").GetDateTimeOffset() == default &&
            bill.GetProperty("reads").GetInt64() == 0 && bill.GetProperty("writes").GetInt64() == 0 && bill.GetProperty("logicalBytes").GetInt64() == 0 &&
            bill.GetProperty("baseCents").GetInt64() == 2000 && bill.GetProperty("writeCents").GetInt64() == 0 &&
            bill.GetProperty("storageCents").GetInt64() == 0 && bill.GetProperty("totalCents").GetInt64() == 0 &&
            !bill.TryGetProperty("alert", out _),
            "An incomplete or overflowing metering aggregate publishes no invented period, partial counters, variable charges or alert; the copied base contract remains known");
        }

        private static async Task CoherentPricingFailureAsync(JsonElement bill, DateTimeOffset month)
        {
            await CheckAsync(
            !bill.GetProperty("available").GetBoolean() && bill.GetProperty("reportOnly").GetBoolean() && !bill.GetProperty("capReached").GetBoolean() &&
            bill.GetProperty("error").GetString() == InvalidAmount && bill.GetProperty("periodStart").GetDateTimeOffset() == month &&
            bill.GetProperty("periodEnd").GetDateTimeOffset() > bill.GetProperty("periodStart").GetDateTimeOffset() &&
            bill.GetProperty("reads").GetInt64() == 123456 && bill.GetProperty("writes").GetInt64() == 106000000 &&
            bill.GetProperty("logicalBytes").GetInt64() == 12_000_000_000L && bill.GetProperty("baseCents").GetInt64() == 2000 &&
            bill.GetProperty("writeCents").GetInt64() == 0 && bill.GetProperty("storageCents").GetInt64() == 0 && bill.GetProperty("totalCents").GetInt64() == 0 &&
            bill.GetProperty("writeCentsPerMillion").GetInt64() == long.MaxValue && !bill.TryGetProperty("alert", out _),
            "Pricing overflow preserves the actual coherent UTC-month metering and valid configured rate without publishing an amount or cap alert");
        }

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

        private static JsonElement Organization(JsonElement observed, string organization)
        {
            return observed.GetProperty("billing").GetProperty("organizations").EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == organization).GetProperty("document");
        }

        private static JsonElement[] Alerts(JsonElement observed, string organization)
        {
            return observed.GetProperty("billing").GetProperty("notifications").EnumerateArray()
                    .Where(row => row.GetProperty("OrganizationId").GetString() == organization && row.GetProperty("Type").GetString() == "internal-cap").ToArray();
        }

        private static async Task NoStripeAsync(JsonElement observed)
        {
            await CheckAsync(observed.GetProperty("billing").GetProperty("stripeRequests").GetArrayLength() == 0,
                    "Internal configuration and reporting do not contact Stripe or create payment resources");
        }

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
        private static async Task CheckAsync(bool value, string reason)
        {
            await Assert.That(value).IsTrue().Because(reason);
        }

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
            public string Snapshot { get { lock (gate) { return ToString(); } } }
            public override void WriteLine(string? value)
            {
                lock (gate)
                {
                    base.WriteLine(value);
                    const string prefix = "Approve CLI access: ";
                    if (value?.StartsWith(prefix, StringComparison.Ordinal) == true)
                    {
                        prompt.TrySetResult(value[prefix.Length..]);
                    }
                }
            }
        }
    }
}
